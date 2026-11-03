#!/usr/bin/env python3
"""Host data acquisition for the load test (run as root: RAPL energy counters are root-only).

Channels at 10 Hz: CPU (root cgroup usage in us + /proc/stat per core), per-core frequency, temperatures, RAPL energy
(package, core, psys), PSI stall totals, eno1 bytes, nvme sectors, context switches, interrupts, run queue, and the
cgroup CPU/memory of the load-test containers. A 2 Hz DNS probe against Technitium doubles as the kill switch: when the
home DNS slows down (6 probes in a row over 250 ms or lost) or the CPU passes 98 degC, the load-test nginx is stopped.

usage: sampler.py <outdir> <container> [<container> ...]
"""
import os, sys, time, socket, struct, random, subprocess, threading

OUT = sys.argv[1]
CONTAINERS = sys.argv[2:]
RATE_HZ = 10
DNS_SERVER = ("192.168.1.6", 53)
KILL_CONTAINER = "pt-lt-nginx"
os.makedirs(OUT, exist_ok=True)
events = open(os.path.join(OUT, "events.log"), "a", buffering=1)


def log(msg):
    events.write(f"{time.time():.3f} {msg}\n")


def read(path):
    try:
        with open(path) as f:
            return f.read()
    except OSError:
        return ""


def first_int(path, default=-1):
    s = read(path).strip()
    try:
        return int(s.split()[0])
    except (ValueError, IndexError):
        return default


def kv(path):
    out = {}
    for line in read(path).splitlines():
        p = line.split()
        if len(p) == 2:
            out[p[0]] = int(p[1])
    return out


# ------------------------------------------------------------------ discovery
cg_paths = {}
for name in CONTAINERS:
    cid = subprocess.run(["docker", "inspect", "-f", "{{.Id}}", name], capture_output=True, text=True).stdout.strip()
    cg_paths[name] = f"/sys/fs/cgroup/system.slice/docker-{cid}.scope" if cid else None
    log(f"cgroup {name} -> {cg_paths[name]}")

hwmon = {}
for h in sorted(os.listdir("/sys/class/hwmon")):
    base = f"/sys/class/hwmon/{h}"
    name = read(f"{base}/name").strip()
    for f in sorted(os.listdir(base)):
        if f.startswith("temp") and f.endswith("_input"):
            label = read(f"{base}/{f.replace('_input', '_label')}").strip().replace(" ", "_") or f.replace("_input", "")
            if name != "iwlwifi_1":
                hwmon[f"t_{name}_{label}"] = f"{base}/{f}"

rapl = {}
for d in sorted(os.listdir("/sys/class/powercap")):
    if d.startswith("intel-rapl:"):
        rapl[f"e_{read(f'/sys/class/powercap/{d}/name').strip()}"] = f"/sys/class/powercap/{d}/energy_uj"

ncpu = os.cpu_count()
freq_paths = [f"/sys/devices/system/cpu/cpu{i}/cpufreq/scaling_cur_freq" for i in range(ncpu)]

header = ["t", "late_us", "cpu_usage_us"]
header += [f"cpu{i}_{k}" for i in range(ncpu) for k in ("busy", "total")]
header += [f"freq{i}_khz" for i in range(ncpu)]
header += list(hwmon) + list(rapl)
header += ["psi_cpu_some_us", "psi_mem_some_us", "psi_io_some_us", "psi_io_full_us"]
header += ["net_rx", "net_tx", "disk_rd_sect", "disk_wr_sect", "disk_io_ms", "ctxt", "intr", "procs_running", "load1", "mem_avail_kb"]
for n in CONTAINERS:
    header += [f"{n}_cpu_us", f"{n}_mem", f"{n}_thr_us"]


def psi(path):
    some = full = -1
    for line in read(path).splitlines():
        tot = int(line.rsplit("total=", 1)[1])
        if line.startswith("some"):
            some = tot
        else:
            full = tot
    return some, full


def sample(late_us):
    row = [f"{time.time():.4f}", str(late_us), str(kv("/sys/fs/cgroup/cpu.stat").get("usage_usec", -1))]
    stat = read("/proc/stat").splitlines()
    per = {}
    ctxt = intr = running = -1
    for line in stat:
        p = line.split()
        if p[0].startswith("cpu") and p[0] != "cpu":
            v = list(map(int, p[1:9]))
            per[p[0]] = (v[0] + v[1] + v[2] + v[5] + v[6] + v[7], sum(v))
        elif p[0] == "ctxt":
            ctxt = int(p[1])
        elif p[0] == "intr":
            intr = int(p[1])
        elif p[0] == "procs_running":
            running = int(p[1])
    for i in range(ncpu):
        b, t = per.get(f"cpu{i}", (-1, -1))
        row += [str(b), str(t)]
    row += [str(first_int(p)) for p in freq_paths]
    row += [str(first_int(p)) for p in hwmon.values()]
    row += [str(first_int(p)) for p in rapl.values()]
    cs, _ = psi("/proc/pressure/cpu")
    ms, _ = psi("/proc/pressure/memory")
    ios, iof = psi("/proc/pressure/io")
    row += [str(cs), str(ms), str(ios), str(iof)]
    rx = tx = -1
    for line in read("/proc/net/dev").splitlines():
        if line.strip().startswith("eno1:"):
            p = line.split(":", 1)[1].split()
            rx, tx = int(p[0]), int(p[8])
    rd = wr = io = -1
    for line in read("/proc/diskstats").splitlines():
        p = line.split()
        if len(p) > 12 and p[2] == "nvme0n1":
            rd, wr, io = int(p[5]), int(p[9]), int(p[12])
    load1 = read("/proc/loadavg").split()[0]
    mem = kv_mem()
    row += [str(rx), str(tx), str(rd), str(wr), str(io), str(ctxt), str(intr), str(running), load1, str(mem)]
    for n in CONTAINERS:
        base = cg_paths.get(n)
        if base:
            st = kv(f"{base}/cpu.stat")
            row += [str(st.get("usage_usec", -1)), str(first_int(f"{base}/memory.current")), str(st.get("throttled_usec", -1))]
        else:
            row += ["-1", "-1", "-1"]
    return row


def kv_mem():
    for line in read("/proc/meminfo").splitlines():
        if line.startswith("MemAvailable:"):
            return int(line.split()[1])
    return -1


# ------------------------------------------------------------------ DNS probe and kill switch
killed = False
dns_bad = 0


def dns_query():
    qid = random.randint(0, 65535)
    q = struct.pack(">HHHHHH", qid, 0x0100, 1, 0, 0, 0)
    for part in "www.lab463.com".split("."):
        q += bytes([len(part)]) + part.encode()
    q += b"\x00" + struct.pack(">HH", 1, 1)
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(1.0)
    t0 = time.perf_counter()
    try:
        s.sendto(q, DNS_SERVER)
        while True:
            data, _ = s.recvfrom(2048)
            if data[:2] == q[:2]:
                return (time.perf_counter() - t0) * 1e6
    except OSError:
        return -1
    finally:
        s.close()


def kill(reason):
    global killed
    if killed:
        return
    killed = True
    log(f"KILL SWITCH: {reason}; stopping {KILL_CONTAINER}")
    subprocess.run(["docker", "stop", "-t", "1", KILL_CONTAINER])


def dns_loop():
    global dns_bad
    with open(os.path.join(OUT, "dns.csv"), "w", buffering=1) as f:
        f.write("t,rtt_us\n")
        nxt = time.time()
        while True:
            t = time.time()
            rtt = dns_query()
            f.write(f"{t:.3f},{rtt:.0f}\n")
            dns_bad = dns_bad + 1 if (rtt < 0 or rtt > 250_000) else 0
            if dns_bad >= 6:
                kill(f"DNS {dns_bad} probes in a row over 250 ms or lost")
            nxt += 0.5
            time.sleep(max(0, nxt - time.time()))


threading.Thread(target=dns_loop, daemon=True).start()

# ------------------------------------------------------------------ main 10 Hz loop (absolute schedule)
with open(os.path.join(OUT, "host.csv"), "w", buffering=1 << 16) as f:
    f.write(",".join(header) + "\n")
    period = 1.0 / RATE_HZ
    nxt = time.time() + period
    log(f"sampler started, {len(header)} channels")
    last_flush = time.time()
    while True:
        now = time.time()
        if now < nxt:
            time.sleep(nxt - now)
        late = int((time.time() - nxt) * 1e6)
        row = sample(late)
        f.write(",".join(row) + "\n")
        temp = max((first_int(p) for k, p in hwmon.items() if "coretemp" in k), default=0)
        if temp > 98000:
            kill(f"CPU temperature {temp / 1000:.1f} degC")
        nxt += period
        if time.time() - nxt > 1:  # fell behind (suspended?): resynchronise
            nxt = time.time() + period
        if time.time() - last_flush > 2:
            f.flush()
            last_flush = time.time()
