#!/usr/bin/env python3
"""Turns one load-test run (client CSVs from LoadLab + server CSVs from the home server) into report data (JSON).

usage: analyze.py <clientRunDir> <serverDataDir> <out.json>

Every number in the report comes from here; the report page only draws. Times are put on the server clock (the client
clock offset is estimated from the X-T header nginx stamps on every response) and expressed in seconds since START.
"""
import json, math, sys, re
from datetime import datetime, timezone
import numpy as np
import pandas as pd
from scipy import signal, optimize

CLIENT, SERVER, OUT = sys.argv[1], sys.argv[2], sys.argv[3]
R = {}  # the report data


def rnd(x, n=3):
    """JSON-friendly rounding (NaN -> None) of scalars and arrays."""
    if isinstance(x, (list, tuple, np.ndarray, pd.Series)):
        return [rnd(v, n) for v in x]
    if x is None:
        return None
    try:
        f = float(x)
    except (TypeError, ValueError):
        return x
    if not math.isfinite(f):
        return None
    return round(f, n)


# ===================================================================== load
phases = pd.read_csv(f"{CLIENT}/phases.csv")
req = pd.read_csv(f"{CLIENT}/requests.csv")
sig = pd.read_csv(f"{CLIENT}/signalr.csv")
e2e = pd.read_csv(f"{CLIENT}/e2e.csv")
rate = pd.read_csv(f"{CLIENT}/rate.csv")
wifi = pd.read_csv(f"{CLIENT}/wifi.csv")
host = pd.read_csv(f"{SERVER}/host/host.csv")
jit = pd.read_csv(f"{SERVER}/host/jitter.csv")
dns = pd.read_csv(f"{SERVER}/host/dns.csv")
ngx = pd.read_csv(f"{SERVER}/nginx/access.csv", header=None,
                  names=["msec", "rt", "urt", "uct", "status", "cls", "bytes", "reqlen", "conn", "connreq"])
pg = pd.read_csv(f"{SERVER}/pg/pgstat.csv", header=None,
                 names=["ms", "commit", "rollback", "ins", "ret", "fetch", "bread", "bhit", "active", "conns", "lockwait",
                        "walrec", "walbytes", "walwrite", "walsync", "ckpt_timed", "ckpt_req", "ckpt_buf", "ckpt_wt", "ckpt_st",
                        "fix_ins", "dbsize"])
cnt = pd.read_csv(f"{SERVER}/counters/counters.csv")
events_log = open(f"{SERVER}/host/events.log").read().splitlines()

# ===================================================================== clock offset (client -> server)
ok = req[(req.status == 202) & (req.server_ms > 0)].copy()
ok["send_us"] = ok.intended_us + ok.send_delay_us
ok["off_ms"] = ok.server_ms - (ok.send_us + ok.service_us / 2) / 1000
fast = ok[ok.service_us <= ok.service_us.quantile(0.03)]
OFFSET_MS = float(fast.off_ms.median())
# drift: offset of the fastest requests in 10-minute windows
fast_w = ok[ok.service_us <= ok.service_us.quantile(0.10)].copy()
fast_w["win"] = (fast_w.send_us // 600e6).astype(int)
drift = fast_w.groupby("win").agg(t=("send_us", "median"), off=("off_ms", "median"), n=("off_ms", "size"))
start_row = phases[phases.phase == "START"].iloc[0]
T0 = 0.0


# the PC clock drifts against the server (a few ppm): a linear fit of the window offsets
if len(drift) >= 2:
    DRIFT_B, DRIFT_A = np.polyfit(drift.t.values / 1e6, drift.off.values, 1)  # ms per s, ms
else:
    DRIFT_B, DRIFT_A = 0.0, OFFSET_MS


def c2s(us):
    """client microseconds -> seconds since START on the server clock (drift-corrected offset)"""
    us = np.asarray(us, dtype=float)
    return (us / 1000 + DRIFT_A + DRIFT_B * us / 1e6) / 1000 - T0


T0 = float(c2s(start_row.start_us))  # server-clock seconds of START (c2s with T0 = 0)
R["clock"] = {
    "offset_ms": rnd(OFFSET_MS, 2),
    "spread_ms": rnd(float(fast.off_ms.quantile(0.84) - fast.off_ms.quantile(0.16)) / 2, 2),
    "drift": {"t": rnd(c2s(drift.t.values), 0), "off": rnd(drift.off.values - OFFSET_MS, 2)},
    "ppm": rnd(DRIFT_B * 1000, 2),
    "resid_ms": rnd(float(np.std(drift.off.values - (DRIFT_A + DRIFT_B * drift.t.values / 1e6))), 2),
    "n": int(len(fast)),
}

# ===================================================================== phases
ph = phases[phases.phase != "START"].copy()
ph["t0"] = c2s(ph.start_us)
ph["t1"] = c2s(ph.end_us)
markers = ph[ph.t1 - ph.t0 < 0.5]
ph = ph[ph.t1 - ph.t0 >= 0.5]
R["phases"] = [{"name": r.phase, "t0": rnd(r.t0, 1), "t1": rnd(r.t1, 1), "params": r.params if isinstance(r.params, str) else ""}
               for r in ph.itertuples()]
R["markers"] = [{"name": r.phase, "t": rnd(r.t0, 1), "params": r.params if isinstance(r.params, str) else ""} for r in markers.itertuples()]
T_END = float(ph.t1.max())


def phase(name):
    m = ph[ph.phase == name]
    return (float(m.t0.iloc[0]), float(m.t1.iloc[0])) if len(m) else None


def phases_like(prefix):
    return [(r.phase, float(r.t0), float(r.t1), r.params) for r in ph.itertuples() if r.phase.startswith(prefix)]


def window(df, t0, t1, col="t"):
    return df[(df[col] >= t0) & (df[col] < t1)]


# ===================================================================== host channels (10 Hz)
host["t"] = host.t - T0
host = host[(host.t >= -60) & (host.t <= T_END + 60)].reset_index(drop=True)
dt = host.t.diff()
H = pd.DataFrame({"t": host.t})


def rate_of(col, scale=1.0, wrap=None):
    d = host[col].diff()
    if wrap:
        d = d.where(d >= 0, d + wrap)
    return d / dt * scale


H["cpu"] = rate_of("cpu_usage_us", 1e-6)                    # cores (0..2)
for c in ("pt-lt-app", "pt-lt-db", "pt-lt-nginx"):
    H[c.replace("pt-lt-", "")] = rate_of(f"{c}_cpu_us", 1e-6)
H["other"] = H.cpu - H.app - H.db - H.nginx
H["freq"] = host[[c for c in host.columns if c.startswith("freq")]].mean(axis=1) / 1e6  # GHz
H["temp"] = host.t_coretemp_Package_id_0 / 1000
H["temp_nvme"] = host.t_nvme_Composite / 1000
H["temp_acpi"] = host.t_acpitz_temp1 / 1000
RAPL_WRAP = 262143328850
H["p_pkg"] = rate_of("e_package-0", 1e-6, RAPL_WRAP)
H["p_core"] = rate_of("e_core", 1e-6, RAPL_WRAP)
H["p_psys"] = rate_of("e_psys", 1e-6, RAPL_WRAP)
H["psi_cpu"] = rate_of("psi_cpu_some_us", 1e-4)              # % of time some task waited for CPU
H["psi_io"] = rate_of("psi_io_some_us", 1e-4)
H["psi_mem"] = rate_of("psi_mem_some_us", 1e-4)
H["net_tx"] = rate_of("net_tx", 1e-6)                        # MB/s
H["net_rx"] = rate_of("net_rx", 1e-6)
H["disk_wr"] = rate_of("disk_wr_sect", 512e-6)
H["ctxt"] = rate_of("ctxt", 1e-3)                            # thousands/s
H["intr"] = rate_of("intr", 1e-3)
H["runq"] = host.procs_running
H["app_mem"] = host["pt-lt-app_mem"] / 2**20
H["db_mem"] = host["pt-lt-db_mem"] / 2**20
H["late"] = host.late_us / 1000
H = H.iloc[1:].replace([np.inf, -np.inf], np.nan)
# clip the rare RAPL/cgroup glitches (a counter read in the middle of an update)
for c in ("p_pkg", "p_core", "p_psys"):
    H.loc[(H[c] < 0) | (H[c] > 60), c] = np.nan
for c in ("cpu", "app", "db", "nginx"):
    H.loc[(H[c] < 0) | (H[c] > 2.2), c] = np.nan
H10 = H.copy()

# ===================================================================== per-second series
sec = lambda t: np.floor(t).astype(int)
H["s"] = sec(H.t)
H1 = H.groupby("s").mean(numeric_only=True)
grid = np.arange(int(np.floor(H1.index.min())), int(np.ceil(T_END)) + 1)
H1 = H1.reindex(grid)

# nginx: server-side latency of the position POSTs
ngx["t"] = ngx.msec - T0
ngx["rt_ms"] = ngx.rt * 1000
ngx["urt_ms"] = pd.to_numeric(ngx.urt.astype(str).str.split(",").str[-1], errors="coerce") * 1000
pos = ngx[ngx.cls == "pos"].copy()
pos["s"] = sec(pos.t)
g = pos.groupby("s")
S1 = pd.DataFrame(index=grid)
S1["ok"] = pos[pos.status == 202].groupby("s").size()
S1["e503"] = pos[pos.status == 503].groupby("s").size()
S1["e429"] = pos[pos.status == 429].groupby("s").size()
S1["eother"] = pos[~pos.status.isin([202, 503, 429])].groupby("s").size()
S1 = S1.fillna(0)
okpos = pos[pos.status == 202]
q = okpos.groupby("s").rt_ms.quantile
S1["p50"] = q(0.5)
S1["p90"] = q(0.9)
S1["p99"] = q(0.99)
S1["pmax"] = okpos.groupby("s").rt_ms.max()

# client: target and dispatched rate
rate["t"] = c2s(rate.t_us)
rate["s"] = sec(rate.t)
S1["target"] = rate.groupby("s").target_rps.mean()
S1["dispatched"] = rate.groupby("s").dispatched.sum()
S1["inflight"] = rate.groupby("s").inflight.max()
S1["spectators"] = rate.groupby("s").spectators.max()

# client-side latency (includes WiFi and the generator)
req["t"] = c2s(req.intended_us)
req["s"] = sec(req.t)
creq = req[(req.kind == "load") & (req.status == 202)]
S1["c_p50"] = creq.groupby("s").latency_us.quantile(0.5) / 1000
S1["c_p99"] = creq.groupby("s").latency_us.quantile(0.99) / 1000

# broadcast: organizer LiveUpdate (server build time "at", the gap between two builds, payload)
org = sig[sig.kind == "org"].copy()
org["at"] = org.at_ms / 1000 - T0
org["recv"] = c2s(org.recv_us)
org = org.sort_values("at")
org["period"] = org["at"].diff()
org["lag"] = org.recv - org["at"]            # delivery: built on the server -> received by the client
org["s"] = sec(org["at"])
S1["bc_period"] = org.groupby("s").period.mean()
S1["bc_kb"] = org.groupby("s").bytes.mean() / 1000
S1["bc_lag"] = org.groupby("s").lag.mean()
S1[["bc_period", "bc_kb", "bc_lag"]] = S1[["bc_period", "bc_kb", "bc_lag"]].ffill(limit=4)

# .NET counters (UTC timestamps at 1 s)
cnt["ts"] = (pd.to_datetime(cnt.Timestamp, format="%m/%d/%Y %H:%M:%S", utc=True) - pd.Timestamp(0, tz="UTC")).dt.total_seconds() - T0
cnt["s"] = sec(cnt.ts)
cnt["name"] = cnt["Counter Name"]


def counter(prefix, contains=None, agg="sum"):
    m = cnt.name.str.startswith(prefix)
    if contains:
        m &= cnt.name.str.contains(contains, regex=False)
    sub = cnt[m]
    return sub.groupby("s")["Mean/Increment"].agg(agg).reindex(grid)


S1["gc0"] = counter("dotnet.gc.collections", "gen0")
S1["gc1"] = counter("dotnet.gc.collections", "gen1")
S1["gc2"] = counter("dotnet.gc.collections", "gen2")
S1["gc_pause_ms"] = counter("dotnet.gc.pause.time") * 1000
S1["alloc_mb"] = counter("dotnet.gc.heap.total_allocated") / 2**20
S1["heap_mb"] = counter("dotnet.gc.last_collection.heap.size") / 2**20
S1["tp_queue"] = counter("dotnet.thread_pool.queue.length")
S1["tp_threads"] = counter("dotnet.thread_pool.thread.count")
S1["lock"] = counter("dotnet.monitor.lock_contentions")
S1["jit_ms"] = counter("dotnet.jit.compilation.time") * 1000
S1["db_pending"] = counter("db.client.connection.npgsql.pending_requests")
S1["db_p99_ms"] = counter("db.client.operation.duration", "Percentile=99", "max") * 1000
S1["sr_conns"] = counter("signalr.server.active_connections")
S1["exc"] = counter("dotnet.exceptions")

# postgres
pg["t"] = pg.ms / 1000 - T0
pg["s"] = sec(pg.t)
pgs = pg.groupby("s").last().reindex(grid)
S1["pg_tps"] = pgs.commit.diff()
S1["pg_ins"] = pgs.ins.diff()
S1["pg_wal_kb"] = pgs.walbytes.diff() / 1024
S1["pg_ckpt_buf"] = pgs.ckpt_buf.diff()
S1["pg_size_mb"] = pgs.dbsize / 2**20

# neighbours and instruments
dns["t"] = dns.t - T0
dns["s"] = sec(dns.t)
S1["dns_ms"] = dns[dns.rtt_us >= 0].groupby("s").rtt_us.mean() / 1000
jit["t"] = jit.t - T0
jit["s"] = sec(jit.t)
S1["jit_p99"] = jit.groupby("s").p99_us.mean() / 1000
S1["jit_max"] = jit.groupby("s").max_us.mean() / 1000
wifi["t"] = c2s(wifi.t_us)
wifi["s"] = sec(wifi.t)
S1["wifi_ms"] = wifi[wifi.rtt_us > 0].groupby("s").rtt_us.median() / 1000

for c in ("cpu", "app", "db", "nginx", "other", "freq", "temp", "temp_nvme", "p_pkg", "p_core", "p_psys", "psi_cpu", "psi_io",
          "net_tx", "net_rx", "disk_wr", "ctxt", "app_mem", "runq"):
    S1[c] = H1[c]

S1 = S1[(S1.index >= 0) & (S1.index <= T_END)]
STRIP = ["target", "ok", "e503", "p50", "p99", "c_p99", "cpu", "app", "db", "nginx", "other", "freq", "temp", "temp_nvme", "p_psys", "p_pkg",
         "p_core", "psi_cpu", "psi_io", "bc_period", "bc_kb", "bc_lag", "gc0", "gc2", "gc_pause_ms", "heap_mb", "alloc_mb", "tp_queue", "tp_threads",
         "db_pending", "pg_tps", "pg_wal_kb", "pg_size_mb", "dns_ms", "jit_p99", "wifi_ms", "net_tx", "disk_wr", "spectators", "inflight",
         "ctxt", "app_mem", "lock"]
R["series"] = {"t": [int(i) for i in S1.index]}
for c in STRIP:
    R["series"][c] = rnd(S1[c].values, 3)

# ===================================================================== latency heatmap (server-side, 10 s columns, log bins)
edges = np.logspace(np.log10(0.3), np.log10(30000), 61)
col_w = 10
okpos_t = okpos[(okpos.t >= 0) & (okpos.t <= T_END)]
cols = np.arange(0, T_END + col_w, col_w)
Hm, _, _ = np.histogram2d(okpos_t.t, okpos_t.rt_ms.clip(edges[0], edges[-1] * 0.999), bins=[cols, edges])
R["heatmap"] = {"t": rnd(cols[:-1], 0), "edges": rnd(edges, 3), "z": Hm.astype(int).T.tolist(), "col_s": col_w}

# ===================================================================== per-phase table
def pstats(name, t0, t1, params):
    p = okpos[(okpos.t >= t0) & (okpos.t < t1)]
    allp = pos[(pos.t >= t0) & (pos.t < t1)]
    c = req[(req.kind == "load") & (req.t >= t0) & (req.t < t1)]
    h = H10[(H10.t >= t0) & (H10.t < t1)]
    o = org[(org["at"] >= t0) & (org["at"] < t1)]
    dur = t1 - t0
    target = rate[(rate.t >= t0) & (rate.t < t1)].target_rps.mean()
    qs = p.rt_ms.quantile([0.5, 0.9, 0.99, 0.999]).values if len(p) else [np.nan] * 4
    cq = (c[c.status == 202].latency_us / 1000).quantile([0.5, 0.99]).values if len(c) else [np.nan] * 2
    return {
        "name": name, "t0": rnd(t0, 1), "t1": rnd(t1, 1), "params": params if isinstance(params, str) else "",
        "target": rnd(target, 1), "achieved": rnd(len(p) / dur, 1),
        "err_rate": rnd((len(allp) - len(p)) / len(allp) if len(allp) else 0, 4),
        "n503": int((allp.status == 503).sum()), "n429": int((allp.status == 429).sum()),
        "p50": rnd(qs[0], 2), "p90": rnd(qs[1], 2), "p99": rnd(qs[2], 2), "p999": rnd(qs[3], 2),
        "c_p50": rnd(cq[0], 2), "c_p99": rnd(cq[1], 2),
        "cpu": rnd(h.cpu.mean(), 3), "app": rnd(h.app.mean(), 3), "db": rnd(h.db.mean(), 3), "other": rnd(h.other.mean(), 3),
        "psys": rnd(h.p_psys.mean(), 2), "pkg": rnd(h.p_pkg.mean(), 2), "temp": rnd(h.temp.mean(), 1), "temp_max": rnd(h.temp.max(), 1),
        "freq": rnd(h.freq.mean(), 3), "psi": rnd(h.psi_cpu.mean(), 1),
        "bc_period": rnd(o.period.mean(), 3), "bc_period_p95": rnd(o.period.quantile(0.95), 3), "bc_kb": rnd(o.bytes.mean() / 1000, 1),
        "tx": rnd(h.net_tx.mean(), 3),
    }


R["phase_stats"] = [pstats(n, a, b, p) for n, a, b, p in phases_like("E")]
PS = {s["name"]: s for s in R["phase_stats"]}
idle = PS.get("E0 noise floor")

# ===================================================================== E1 field size
field = []
for n, a, b, p in phases_like("E1 field"):
    s = pstats(n, a + 30, b, p)  # skip the first 30 s: the field grows into the view
    s["n"] = int(n.split()[-1])
    field.append(s)
if field:
    xs = np.array([f["n"] for f in field], float)
    ys = np.array([f["app"] for f in field], float)
    k = np.polyfit(xs, ys, 1)
    # broadcast work per participant: app CPU-seconds per second / broadcasts per second / participants
    per = [f["app"] * (f["bc_period"] or 1.5) / f["n"] * 1e3 for f in field]  # ms of CPU per participant per broadcast
    R["field"] = {"rows": field, "fit_app": rnd(k, 6), "cpu_ms_per_participant": rnd(per, 3)}

# ===================================================================== step response (E2)
st = phase("E2 step up")
if st:
    a, b = st
    pre = phase("E2 pre-step")
    w0, w1 = a - 60, min(b + 180, T_END)
    W = H10[(H10.t >= w0) & (H10.t < w1)].copy()
    W["tr"] = W.t - a
    sm = W.set_index("tr")
    rs = lambda c: rnd(sm[c].rolling(5, center=True, min_periods=1).mean().values, 3)
    step = {"t": rnd(W.t.values - a, 1), "app": rs("app"), "cpu": rs("cpu"), "db": rs("db"), "psys": rs("p_psys"),
            "temp": rnd(W.temp.values, 1), "freq": rs("freq")}
    S = S1[(S1.index >= w0) & (S1.index < w1)]
    step["s_t"] = [int(i - a) for i in S.index]
    step["p50"] = rnd(S.p50.values, 2)
    step["p99"] = rnd(S.p99.values, 2)
    step["ok"] = rnd(S.ok.values, 0)
    step["bc_period"] = rnd(S.bc_period.values, 3)
    step["jit_ms"] = rnd(S.jit_ms.values, 2)

    # temperature: first-order fit T(t) = T0 + dT (1 - exp(-t/tau)) on the step up
    up = W[(W.t >= a) & (W.t < b)]
    tt = (up.t - a).values
    TT = up.temp.rolling(10, center=True, min_periods=1).mean().values
    try:
        (T0f, dTf, tauf), _ = optimize.curve_fit(lambda t, T0_, dT, tau: T0_ + dT * (1 - np.exp(-t / tau)), tt, TT,
                                                  p0=[TT[:20].mean(), TT[-100:].mean() - TT[:20].mean(), 30], maxfev=20000)
        step["temp_fit"] = {"T0": rnd(T0f, 2), "dT": rnd(dTf, 2), "tau": rnd(tauf, 1)}
    except Exception as ex:  # noqa: BLE001
        step["temp_fit"] = None
    # app CPU: rise time 10..90 % and settling (+-5 % band) on the 1 s means
    app1 = S.app[(S.index >= a - 30) & (S.index < b)]
    base = float(S.app[(S.index >= a - 30) & (S.index < a)].mean())
    final = float(S.app[(S.index >= b - 120) & (S.index < b)].mean())
    rel = (app1 - base) / (final - base) if final != base else app1 * 0
    after = rel[rel.index >= a]

    def first(cond):
        idx = after.index[cond(after.values)]
        return float(idx[0] - a) if len(idx) else None
    t10, t90 = first(lambda v: v >= 0.1), first(lambda v: v >= 0.9)
    outside = after.index[np.abs(after.values - 1) > 0.05]
    step["app_metrics"] = {"base": rnd(base, 3), "final": rnd(final, 3), "rise_s": rnd(t90 - t10, 1) if t10 is not None and t90 is not None else None,
                           "peak": rnd(float(app1[app1.index >= a].max()), 3), "overshoot_pct": rnd((float(app1[app1.index >= a].max()) - final) / (final - base) * 100, 1),
                           "settle_s": rnd(float(outside[-1] - a) if len(outside) else 0, 1)}
    lat = S.p99[(S.index >= a) & (S.index < b)]
    step["lat_metrics"] = {"p99_first10": rnd(lat.iloc[:10].max(), 1), "p99_steady": rnd(lat.iloc[-120:].median(), 2)}
    # after the step down: how long the backlog keeps answering (requests still finishing, 503s) — completions of load requests
    loadreq = req[(req.kind == "load")]
    done_t = (loadreq.t + loadreq.latency_us / 1e6)
    late = loadreq[(loadreq.t < b) & (done_t >= b)]
    step["drain"] = {"after_s": rnd(float((done_t[(loadreq.t < b)].max()) - b), 1), "late_requests": int(len(late)),
                     "late_503": int((late.status == 503).sum()), "late_timeouts": int((late.status < 0).sum())}
    ps_up = pos[(pos.t >= a) & (pos.t < b + 60)]
    step["errors"] = {"n503": int((ps_up.status == 503).sum()), "n": int(len(ps_up)), "rate": rnd((ps_up.status == 503).mean(), 4)}
    R["step"] = step

# ===================================================================== staircase (E3)
stairs = []
for n, a, b, p in phases_like("E3 step"):
    s = pstats(n, a + 15, b, p)  # the first 15 s of a step are its transient
    s["rps"] = float(n.split()[-1])
    stairs.append(s)
if stairs:
    idle_psys = idle["psys"] if idle else stairs[0]["psys"]
    for s in stairs:
        s["mj_per_req"] = rnd((s["psys"] - idle_psys) / s["achieved"] * 1000, 2) if s["achieved"] else None
        # Little's law: mean concurrency inside the server
        s["N"] = rnd(s["achieved"] * (s["p50"] or 0) / 1000, 3)
    R["stairs"] = {"rows": stairs, "idle_psys": idle_psys,
                   "saturation": next((m["params"] for m in R["markers"] if m["name"] == "E3 saturation"), None),
                   "capacity": next((m["params"] for m in R["markers"] if m["name"] == "E3 capacity"), None)}

# ===================================================================== chirp -> transfer functions (E4)
ch = phase("E4 chirp")
if ch:
    a, b = ch
    fs = 10.0
    tgrid = np.arange(a, b, 1 / fs)
    # input: requests actually dispatched (client), 100 ms bins on the server clock
    u = np.interp(tgrid, rate.t.values, rate.dispatched.values * 10)
    ys = {
        "cpu": np.interp(tgrid, H10.t, H10.cpu.fillna(0)),
        "app": np.interp(tgrid, H10.t, H10.app.fillna(0)),
        "psys": np.interp(tgrid, H10.t, H10.p_psys.ffill().fillna(0)),
        "temp": np.interp(tgrid, H10.t, H10.temp.ffill()),
    }
    # Tracking filter (lock-in): the chirp phase phi(t) is known, so at each analysis frequency the input and every output are
    # least-squares fitted with sin(phi), cos(phi), offset and drift over a window of >= 4 periods around the moment the sweep
    # passes that frequency. H = Y/U (complex amplitudes); the fit R^2 of the output stands in for coherence.
    prm = ph[ph.phase == "E4 chirp"].params.iloc[0]
    T_ch = float(re.search(r"T (\d+)", prm).group(1))
    f0, f1 = 1 / 400, 1 / 4
    kk = f1 / f0
    tc = tgrid - a
    phi = 2 * np.pi * f0 * T_ch / np.log(kk) * (np.power(kk, tc / T_ch) - 1)
    freqs = np.logspace(np.log10(f0 * 1.6), np.log10(f1 / 1.15), 44)
    bode = {k_: {"mag": [], "phase": [], "coh": [], "lo": [], "hi": []} for k_ in ys}
    fkeep = []

    def amp(y, m, X, with_se=False):
        c, *_ = np.linalg.lstsq(X, y[m], rcond=None)
        fit = X @ c
        res = y[m] - fit
        ss = np.sum((y[m] - y[m].mean()) ** 2)
        r2 = (1 - np.sum(res ** 2) / ss) if ss > 0 else 0.0
        if not with_se:
            return complex(c[1], -c[0]), r2
        # standard error of the sine amplitude; the residual is coloured, so count one independent sample per second
        n_eff = max(4.0, m.sum() / fs)
        return complex(c[1], -c[0]), r2, float(np.sqrt(2 * res.var() / n_eff))

    for fr in freqs:
        t_star = T_ch * np.log(fr / f0) / np.log(kk)
        half = max(2 / fr, 20)
        m = (tc >= t_star - half) & (tc < t_star + half)
        if m.sum() < 100 or tc[m].min() > t_star - half * 0.6 or tc[m].max() < t_star + half * 0.6:
            continue
        X = np.column_stack([np.sin(phi[m]), np.cos(phi[m]), np.ones(m.sum()), tc[m] - t_star])
        U, _ = amp(u, m, X)
        fkeep.append(fr)
        for k_, y in ys.items():
            Y, r2, se = amp(y, m, X, True)
            Hc = Y / U
            bode[k_]["mag"].append(abs(Hc))
            bode[k_]["lo"].append(max(abs(Y) - 2 * se, abs(Y) * 0.02) / abs(U))
            bode[k_]["hi"].append((abs(Y) + 2 * se) / abs(U))
            bode[k_]["phase"].append(np.degrees(np.angle(Hc)))
            bode[k_]["coh"].append(max(0.0, r2))
    for k_ in ys:
        bode[k_] = {"mag": rnd(bode[k_]["mag"], 6), "lo": rnd(bode[k_]["lo"], 6), "hi": rnd(bode[k_]["hi"], 6), "phase": rnd(np.degrees(np.unwrap(np.radians(bode[k_]["phase"]))), 1), "coh": rnd(bode[k_]["coh"], 3)}
    bode["f"] = rnd(fkeep, 5)
    bode["method"] = "lock-in"
    # first-order low-pass fit of the temperature response: |H| = K / sqrt(1 + (2 pi f tau)^2)
    tm = np.array(bode["temp"]["mag"], float)
    tf_ = np.array(bode["f"], float)
    good = np.array(bode["temp"]["coh"], float) > 0.3
    try:
        (Kf, tauf), _ = optimize.curve_fit(lambda f_, K, tau: K / np.sqrt(1 + (2 * np.pi * f_ * tau) ** 2), tf_[good], tm[good], p0=[tm[:3].mean(), 30], maxfev=20000)
        bode["temp_fit"] = {"K": rnd(Kf, 5), "tau": rnd(abs(tauf), 1), "fc": rnd(1 / (2 * np.pi * abs(tauf)), 5), "n": int(good.sum())}
    except Exception:  # noqa: BLE001
        bode["temp_fit"] = None
    # time traces (1 Hz) for the chirp panel
    Wc = S1[(S1.index >= a) & (S1.index < b)]
    bode["trace"] = {"t": [int(i - a) for i in Wc.index], "target": rnd(Wc.target.values, 1), "ok": rnd(Wc.ok.values, 0),
                     "cpu": rnd(Wc.cpu.values, 3), "temp": rnd(Wc.temp.values, 2), "psys": rnd(Wc.p_psys.values, 2), "p50": rnd(Wc.p50.values, 2)}
    R["bode"] = bode

    # phase portrait: power vs temperature over the chirp (hysteresis)
    R["portrait"] = {"psys": rnd(Wc.p_psys.rolling(5, min_periods=1).mean().values, 2), "temp": rnd(Wc.temp.rolling(5, min_periods=1).mean().values, 2),
                     "cpu": rnd(Wc.cpu.values, 3), "t": [int(i - a) for i in Wc.index]}

# ===================================================================== impulses (E5)
imps = phases_like("E5 impulse")
if imps:
    traces = []
    rows = []
    for n, a, b, p in imps:
        t_imp = a + 30
        W = H10[(H10.t >= t_imp - 10) & (H10.t < t_imp + 120)]
        P = pos[(pos.t >= t_imp - 10) & (pos.t < t_imp + 120)]
        bins = np.arange(-10, 120.01, 0.5)
        okc, _ = np.histogram(P[P.status == 202].t - t_imp, bins)
        e503, _ = np.histogram(P[P.status == 503].t - t_imp, bins)
        cpu = np.interp(bins[:-1] + 0.25, W.t - t_imp, W.cpu.fillna(0))
        traces.append({"ok": okc * 2, "e503": e503 * 2, "cpu": cpu})
        burst = req[(req.kind == "load") & (req.t >= t_imp - 0.5) & (req.t < t_imp + 1.5)]
        good = burst[burst.status == 202]
        done_t = (good.t + good.latency_us / 1e6).max() - t_imp if len(good) else np.nan
        rows.append({"name": n, "sent": int(len(burst)), "ok": int(len(good)), "e503": int((burst.status == 503).sum()),
                     "other": int((~burst.status.isin([202, 503])).sum()),
                     "c_p50": rnd(good.latency_us.median() / 1000, 1), "c_p99": rnd(good.latency_us.quantile(0.99) / 1000, 1),
                     "c_max": rnd(good.latency_us.max() / 1000, 1), "drain_s": rnd(done_t, 2)})
    R["impulse"] = {"t": rnd(bins[:-1], 1), "rows": rows,
                    "ok": rnd(np.mean([x["ok"] for x in traces], axis=0), 1), "e503": rnd(np.mean([x["e503"] for x in traces], axis=0), 1),
                    "cpu": rnd(np.mean([x["cpu"] for x in traces], axis=0), 3),
                    "each_ok": [rnd(x["ok"], 0) for x in traces]}

# ===================================================================== fan-out (E6)
fan = []
spec = sig[sig.kind == "spec"].copy()
spec["recv"] = c2s(spec.recv_us)
spec["at"] = spec.at_ms / 1000 - T0
for n, a, b, p in phases_like("E6 spectators"):
    s = pstats(n, a + 20, b, p)
    nn = int(n.split()[-1])
    s["n"] = nn
    sp = spec[(spec.recv >= a + 20) & (spec.recv < b)]
    s["spec_mbps"] = rnd(sp.bytes.sum() / (b - a - 20) / 1e6, 3)
    s["spec_kb"] = rnd(sp.bytes.mean() / 1000, 1) if len(sp) else None
    s["spec_rate"] = rnd(len(sp) / (b - a - 20) / nn, 3) if nn else None
    s["spec_lag_p50"] = rnd((sp.recv - sp["at"]).median(), 3) if len(sp) else None
    s["spec_lag_p99"] = rnd((sp.recv - sp["at"]).quantile(0.99), 3) if len(sp) else None
    fan.append(s)
if fan:
    R["fanout"] = {"rows": fan, "limit": next((m["params"] for m in R["markers"] if m["name"] == "E6 limit"), None)}

# ===================================================================== end-to-end (probe phones -> organizer view)
e2e["send"] = c2s(e2e.send_us)
e2e["recv"] = c2s(e2e.recv_us)
e2e["lat"] = e2e.recv - e2e.send
e2e = e2e[(e2e.lat > 0) & (e2e.lat < 60)]
e2e_ph = []
for name in (["E0 noise floor"] + [s["name"] for s in R["phase_stats"] if s["name"].startswith("E1 field")][-1:]
             + ["E2 step up", "E4 chirp", "E7 soak"]):
    pr = phase(name)
    if not pr:
        continue
    v = np.sort(e2e[(e2e.send >= pr[0]) & (e2e.send < pr[1])].lat.values)
    if len(v) < 5:
        continue
    qq = np.linspace(0, 1, 101)
    e2e_ph.append({"name": name, "n": int(len(v)), "cdf_x": rnd(np.quantile(v, qq), 3), "p50": rnd(np.median(v), 3),
                   "p99": rnd(np.quantile(v, 0.99), 3), "mean": rnd(v.mean(), 3)})
R["e2e"] = {"phases": e2e_ph, "all_p50": rnd(e2e.lat.median(), 3), "all_p99": rnd(e2e.lat.quantile(0.99), 3), "n": int(len(e2e))}

# ===================================================================== spectra
def psd(x, fs, nper):
    x = np.asarray(x, float)
    x = x[np.isfinite(x)]
    f_, P = signal.welch(x - x.mean(), fs, nperseg=min(nper, len(x)), detrend="linear")
    return f_, P


def identify(fr, df, fbc):
    """Names a spectral line by the nearest known periodic source (within two frequency bins)."""
    cands = []
    if fbc:
        cands += [(k * fbc, f"broadcast {k}×" if k > 1 else "broadcast") for k in range(1, 8)]
    cands += [(k * 1.0, f"1 s meranie {k}×" if k > 1 else "1 s meranie (sidecary)") for k in range(1, 6)]
    cands += [(k / 15, f"Prometheus 15 s {k}×" if k > 1 else "Prometheus scrape 15 s") for k in range(1, 6)]
    cands += [(k / 5, f"sondy 5 s {k}×" if k > 1 else "sondy / mobily 5 s") for k in range(1, 6)]
    cands += [(k / 11.5, "MQTT broker ~11,5 s" if k == 1 else f"MQTT broker {k}×") for k in range(1, 5)]  # identified per container (~11.5 s)
    cands += [(1 / 300, "5 min (cron / checkpoint)"), (1 / 60, "1 min")]
    best = min(cands, key=lambda c: abs(c[0] - fr))
    return best[1] if abs(best[0] - fr) <= 2 * df else None


bc_f = {}
for name in ("E0 noise floor", "E7 soak"):
    pr = phase(name)
    if pr:
        per = org[(org["at"] >= pr[0]) & (org["at"] < pr[1])].period.median()
        bc_f[name] = 1 / per if per and per > 0 else None

spectra = {}
for name in ("E0 noise floor", "E7 soak"):
    pr = phase(name)
    if not pr:
        continue
    W = H10[(H10.t >= pr[0]) & (H10.t < pr[1])]
    out = {}
    for c in ("cpu", "p_psys", "late"):
        f_, P = psd(W[c].interpolate().values, 10.0, 2048)
        out[c] = rnd(10 * np.log10(P[1:] + 1e-12), 2)
    out["f"] = rnd(f_[1:], 5)
    # peaks of the CPU spectrum (prominent, above 0.005 Hz)
    Pc = np.array(out["cpu"], float)
    pk, props = signal.find_peaks(Pc, prominence=6, distance=3)
    order = np.argsort(-props["prominences"])[:8]
    out["peaks"] = [{"f": rnd(f_[1:][pk[i]], 4), "db": rnd(Pc[pk[i]], 1), "period_s": rnd(1 / f_[1:][pk[i]], 2), "prom": rnd(props["prominences"][i], 1),
                     "id": identify(f_[1:][pk[i]], f_[1] - f_[0], bc_f.get(name))}
                    for i in sorted(order, key=lambda j: f_[1:][pk[j]]) if f_[1:][pk[i]] > 0.005]
    spectra[name] = out
R["spectra"] = spectra

# spectrogram of the host CPU over the whole run (10 Hz, 204.8 s windows, 50 % overlap)
x = H10.set_index("t").cpu.interpolate().reindex(np.arange(0, T_END, 0.1), method="nearest").fillna(0).values
fS, tS, Sxx = signal.spectrogram(x - np.mean(x), 10.0, nperseg=2048, noverlap=1024, detrend="linear", scaling="density")
keep = (fS > 0) & (fS <= 2.0)
R["spectrogram"] = {"f": rnd(fS[keep], 4), "t": rnd(tS, 0), "z": rnd(10 * np.log10(Sxx[keep] + 1e-9), 1), "df": rnd(fS[1] - fS[0], 5)}

# ===================================================================== jitter, DNS, WiFi distributions per regime
def regime_rows():
    regs = [("Kľud (E0)", "E0 noise floor")]
    sat = [s for s in R["phase_stats"] if s["name"].startswith("E3 step")]
    if sat:
        regs.append(("Saturácia (" + sat[-1]["name"].replace("E3 step ", "") + " req/s)", sat[-1]["name"]))
    regs.append(("Pretek (E7)", "E7 soak"))
    return [(lab, phase(n)) for lab, n in regs if phase(n)]


hist_cols = [c for c in jit.columns if c.startswith("h")]
jit_out, dns_out = [], []
for lab, (a, b) in regime_rows():
    J = jit[(jit.t >= a) & (jit.t < b)]
    hsum = J[hist_cols].sum().values
    jit_out.append({"name": lab, "hist": [int(v) for v in hsum], "p99": rnd(J.p99_us.median(), 1), "p999": rnd(J.p999_us.median(), 1),
                    "max": rnd(J.max_us.max(), 0), "mean": rnd(J.mean_us.mean(), 1)})
    D = dns[(dns.t >= a) & (dns.t < b) & (dns.rtt_us >= 0)].rtt_us / 1000
    lost = int(((dns.t >= a) & (dns.t < b) & (dns.rtt_us < 0)).sum())
    dns_out.append({"name": lab, "p50": rnd(D.median(), 2), "p99": rnd(D.quantile(0.99), 2), "max": rnd(D.max(), 1), "lost": lost, "n": int(len(D)),
                    "q": rnd(D.quantile(np.linspace(0, 1, 51)).values, 3)})
R["jitter"] = {"bins_us": [2 ** i for i in range(len(hist_cols))], "regimes": jit_out}
R["dns"] = dns_out
wv = wifi[wifi.rtt_us > 0].rtt_us / 1000
R["wifi"] = {"p50": rnd(wv.median(), 2), "p90": rnd(wv.quantile(0.9), 2), "p99": rnd(wv.quantile(0.99), 2), "max": rnd(wv.max(), 1),
             "lost": int((wifi.rtt_us < 0).sum()), "n": int(len(wifi)), "q": rnd(wv.quantile(np.linspace(0, 1, 51)).values, 3)}

# ===================================================================== GC <-> latency cross-correlation (1 s, during the soak or the chirp)
pr = phase("E7 soak") or phase("E4 chirp")
if pr:
    W = S1[(S1.index >= pr[0]) & (S1.index < pr[1])]
    a_ = W.gc_pause_ms.fillna(0).values
    b_ = W.p99.interpolate(limit_direction="both").values
    a_ = (a_ - a_.mean()) / (a_.std() or 1)
    b_ = (b_ - b_.mean()) / (b_.std() or 1)
    lags = np.arange(-20, 21)
    xc = [float(np.mean(a_[max(0, -l):len(a_) - max(0, l)] * b_[max(0, l):len(b_) - max(0, -l)])) for l in lags]
    bc = W.bc_period.fillna(W.bc_period.mean()).values
    bcz = (bc - bc.mean()) / (bc.std() or 1)
    xc2 = [float(np.mean(bcz[max(0, -l):len(bcz) - max(0, l)] * b_[max(0, l):len(b_) - max(0, -l)])) for l in lags]
    R["xcorr"] = {"lags": [int(l) for l in lags], "gc_p99": rnd(xc, 3), "bc_p99": rnd(xc2, 3), "phase": "E7 soak" if phase("E7 soak") else "E4 chirp",
                  "gc_total_s": rnd(W.gc_pause_ms.sum() / 1000, 2), "gc2": int(W.gc2.fillna(0).sum()), "gc0": int(W.gc0.fillna(0).sum())}

# ===================================================================== synchronous (phase-resolved) latency vs the broadcast cycle
# "at" of a LiveUpdate is taken inside the runtime lock when the build starts; a POST arriving d seconds later waits for the
# rest of the build (B - d) if the build is still running. Plotting latency against d shows that ramp and its length B.
at_sorted = np.sort(org["at"].dropna().values)
sync = []
for name in ("E0 noise floor", "E2 pre-step", "E3 cooldown", "E4 chirp", "E7 soak"):
    pr = phase(name)
    if not pr:
        continue
    P = pos[(pos.t >= pr[0]) & (pos.t < pr[1]) & (pos.status == 202)].copy()
    if len(P) < 1000:  # the probes alone are phase-locked to the cycle (5 s vs 1.5 s): too few phases
        continue
    start = (P.t - P.rt).values
    idx = np.searchsorted(at_sorted, start, side="right") - 1
    ok_ = idx >= 0
    d = start[ok_] - at_sorted[idx[ok_]]
    lat = P.rt_ms.values[ok_]
    keep = d < 3.0
    d, lat = d[keep], lat[keep]
    # the ramp: the 90th percentile of latency in 50 ms bins of d
    bins_ = np.arange(0, 3.0001, 0.05)
    which = np.digitize(d, bins_) - 1
    env = [float(np.percentile(lat[which == i], 90)) if (which == i).sum() >= 5 else None for i in range(len(bins_) - 1)]
    med = [float(np.median(lat[which == i])) if (which == i).sum() >= 5 else None for i in range(len(bins_) - 1)]
    # build length B: where the median falls back to the floor (2x the median of the late bins)
    valid = [m for m in med if m is not None]
    floor = float(min(valid)) if valid else np.nan
    B = next((float(bins_[i]) for i in range(len(med)) if med[i] is not None and med[i] <= 3 * floor + 5), None)
    rng = np.random.default_rng(1)
    sel = rng.choice(len(d), size=min(2500, len(d)), replace=False)
    sync.append({"name": name, "d": rnd(d[sel], 3), "lat": rnd(lat[sel], 1), "bins": rnd(bins_[:-1] + 0.025, 3), "p90": rnd(env, 1), "p50": rnd(med, 1),
                 "B": rnd(B, 2), "floor": rnd(floor, 1), "n": int(len(d)), "lock_s": rnd(float(S1.lock[(S1.index >= pr[0]) & (S1.index < pr[1])].mean()), 2),
                 "rps": rnd(len(P) / (pr[1] - pr[0]), 1)})
R["sync"] = sync

# ===================================================================== headline KPIs
R["kpi"] = {
    "duration_min": rnd(T_END / 60, 1),
    "requests": int(len(pos)),
    "requests_ok": int((pos.status == 202).sum()),
    "samples": int(len(host) * (len(host.columns) - 1) + len(jit) * 1000 + len(ngx) + len(cnt) + len(pg) * 21),
    "channels": int(len(host.columns) - 1 + 1 + 1 + cnt.name.nunique() + 21),
    "capacity": next((f"{r['rps']:.0f} req/s" for r in reversed(R.get("stairs", {}).get("rows", []))
                      if (r["p99"] or 1e9) <= 1500 and r["err_rate"] <= 0.02), None),
    "temp_max": rnd(H10.temp.max(), 1), "psys_max": rnd(H10.p_psys.quantile(0.999), 1), "psys_idle": rnd(idle["psys"], 2) if idle else None,
    "bc_kb_max": rnd(org.bytes.max() / 1000, 0), "e2e_p50": R["e2e"]["all_p50"], "e2e_p99": R["e2e"]["all_p99"],
    "fixes_stored": int(pgs.fix_ins.max()) if pgs.fix_ins.notna().any() else None,
    "db_mb": rnd(pgs.dbsize.max() / 2**20, 0),
    "kill_switch": [l for l in events_log if "KILL" in l],
    "cpu_model": "Intel Celeron N4505 (Jasper Lake), 2C/2T, 0.8–2.9 GHz, TDP 6–10 W",
}
# one capacity figure: the last stair that met the criterion (the generator's marker floors it at 50 req/s)
if "stairs" in R:
    R["stairs"]["capacity"] = R["kpi"]["capacity"]
# the lock-in temperature fit is not used: at +-20 req/s the response sits below the sensor's 1 degC step (the step test gives tau)
if "bode" in R:
    R["bode"].pop("temp_fit", None)
R["generated"] = datetime.now(timezone.utc).isoformat(timespec="seconds")

with open(OUT, "w", encoding="utf-8") as fh:
    json.dump(R, fh, ensure_ascii=False, separators=(",", ":"))
print("wrote", OUT, f"{len(json.dumps(R)) / 1e6:.2f} MB", "offset", round(OFFSET_MS, 2), "ms; phases", len(R["phases"]))
