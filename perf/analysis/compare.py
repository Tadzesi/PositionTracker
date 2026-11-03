#!/usr/bin/env python3
"""Before/after comparison of two CMP runs (perf/compare.ps1) -> report data (JSON) for the "what the measurement changed" section.

usage: compare.py <beforeClient> <beforeServer> <afterClient> <afterServer> <out.json>

Only the five highlighted points are computed: headline KPIs, the phase-resolved latency (the lock's fingerprint) with its
spectrum, the capacity curve, the mass-start response and a short "why" table. Times are on the server clock, as in analyze.py.
"""
import json, math, sys
from datetime import datetime, timezone
import numpy as np
import pandas as pd
from scipy import signal


def rnd(x, n=3):
    if isinstance(x, (list, tuple, np.ndarray, pd.Series)):
        return [rnd(v, n) for v in x]
    if x is None:
        return None
    try:
        f = float(x)
    except (TypeError, ValueError):
        return x
    return round(f, n) if math.isfinite(f) else None


P99_LIMIT_MS, ERR_LIMIT = 1500, 0.02   # the saturation criterion of the stairs (LoadLab)
RAPL_WRAP = 262143328850


def run(client, server):
    phases = pd.read_csv(f"{client}/phases.csv")
    req = pd.read_csv(f"{client}/requests.csv")
    sig = pd.read_csv(f"{client}/signalr.csv")
    host = pd.read_csv(f"{server}/host/host.csv")
    ngx = pd.read_csv(f"{server}/nginx/access.csv", header=None,
                      names=["msec", "rt", "urt", "uct", "status", "cls", "bytes", "reqlen", "conn", "connreq"])
    cnt = pd.read_csv(f"{server}/counters/counters.csv")

    # client clock -> server clock: offset of the fastest requests, linear drift (as analyze.py)
    ok = req[(req.status == 202) & (req.server_ms > 0)].copy()
    ok["send_us"] = ok.intended_us + ok.send_delay_us
    ok["off_ms"] = ok.server_ms - (ok.send_us + ok.service_us / 2) / 1000
    fw = ok[ok.service_us <= ok.service_us.quantile(0.10)].copy()
    fw["win"] = (fw.send_us // 300e6).astype(int)
    dr = fw.groupby("win").agg(t=("send_us", "median"), off=("off_ms", "median"))
    b_, a_ = np.polyfit(dr.t.values / 1e6, dr.off.values, 1) if len(dr) >= 2 else (0.0, float(fw.off_ms.median()))
    t0 = 0.0

    def c2s(us):
        us = np.asarray(us, dtype=float)
        return (us / 1000 + a_ + b_ * us / 1e6) / 1000 - t0

    t0 = float(c2s(phases[phases.phase == "START"].start_us.iloc[0]))
    ph = phases[phases.phase != "START"].copy()
    ph["t0"], ph["t1"] = c2s(ph.start_us), c2s(ph.end_us)
    markers = ph[ph.t1 - ph.t0 < 0.5]
    ph = ph[ph.t1 - ph.t0 >= 0.5]

    def phase(name):
        m = ph[ph.phase == name]
        return (float(m.t0.iloc[0]), float(m.t1.iloc[0])) if len(m) else None

    # host (10 Hz): CPU of the app container and the platform power
    host["t"] = host.t - t0
    dt = host.t.diff()
    H = pd.DataFrame({"t": host.t})
    H["app"] = host["pt-lt-app_cpu_us"].diff() / dt * 1e-6
    H["cpu"] = host["cpu_usage_us"].diff() / dt * 1e-6
    d = host["e_psys"].diff()
    H["psys"] = d.where(d >= 0, d + RAPL_WRAP) / dt * 1e-6
    H = H.iloc[1:].replace([np.inf, -np.inf], np.nan)
    H.loc[(H.psys < 0) | (H.psys > 60), "psys"] = np.nan
    H.loc[(H.app < 0) | (H.app > 2.2), "app"] = np.nan

    ngx["t"] = ngx.msec - t0
    ngx["rt_ms"] = ngx.rt * 1000
    pos = ngx[ngx.cls == "pos"].copy()
    okpos = pos[pos.status == 202]

    org = sig[sig.kind == "org"].copy()
    org["at"] = org.at_ms / 1000 - t0
    at_sorted = np.sort(org["at"].dropna().values)

    cnt["ts"] = (pd.to_datetime(cnt.Timestamp, format="%m/%d/%Y %H:%M:%S", utc=True) - pd.Timestamp(0, tz="UTC")).dt.total_seconds() - t0
    cnt["name"] = cnt["Counter Name"]

    def counter_in(prefix, a, b, agg):
        m = cnt.name.str.startswith(prefix) & (cnt.ts >= a) & (cnt.ts < b)
        s = cnt[m].groupby(np.floor(cnt[m].ts))["Mean/Increment"].sum()
        return float(getattr(s, agg)()) if len(s) else None

    req["t"] = c2s(req.intended_us)

    def stats(a, b):
        p = okpos[(okpos.t >= a) & (okpos.t < b)]
        allp = pos[(pos.t >= a) & (pos.t < b)]
        h = H[(H.t >= a) & (H.t < b)]
        return {"achieved": len(p) / (b - a), "err": (len(allp) - len(p)) / len(allp) if len(allp) else 0.0,
                "p50": float(p.rt_ms.median()) if len(p) else None, "p99": float(p.rt_ms.quantile(0.99)) if len(p) else None,
                "app": float(h.app.mean()), "cpu": float(h.cpu.mean()), "psys": float(h.psys.mean())}

    out = {}
    # ---------------------------------------------------------------- capacity curve (C1 stairs, first 15 s are the transient)
    stairs = []
    for r in ph.itertuples():
        if r.phase.startswith("C1 step"):
            s = stats(r.t0 + 15, r.t1)
            s["rps"] = float(r.phase.split()[-1])
            s["db_pending"] = counter_in("db.client.connection.npgsql.pending_requests", r.t0 + 15, r.t1, "max")
            s["lock"] = counter_in("dotnet.monitor.lock_contentions", r.t0 + 15, r.t1, "mean")
            s["good"] = (s["p99"] or 1e9) <= P99_LIMIT_MS and s["err"] <= ERR_LIMIT and s["achieved"] >= 0.9 * s["rps"]
            stairs.append(s)
    cap = next((s["rps"] for s in reversed(stairs) if s["good"]), None)
    # the stairs stop at the first failing step: is the capacity a limit, or did the plan run out of steps?
    out["stairs"] = [{k: rnd(v, 4) if not isinstance(v, bool) else v for k, v in s.items()} for s in stairs]
    out["capacity_rps"] = cap
    out["capacity_limited"] = any(not s["good"] for s in stairs)
    present = cnt.name.str.startswith("db.client.connection.npgsql.pending_requests").any()
    out["db_pending_stairs"] = max([s["db_pending"] or 0 for s in stairs]) if present else None
    out["db_pending_at"] = next((s["rps"] for s in stairs if s["db_pending"] and s["db_pending"] == out["db_pending_stairs"]), None)
    out["saturation"] = next((m.params for m in markers.itertuples() if m.phase == "C1 saturation"), None)

    # ---------------------------------------------------------------- same load: the C2 step (40 req/s = 200 phones)
    up, pre = phase("C2 step up"), phase("C2 pre-step")
    s_up = stats(up[0] + 60, up[1])          # steady part
    s_pre = stats(pre[0] + 20, pre[1])
    out["same_load"] = {
        "rps": rnd(s_up["achieved"], 1), "p50": rnd(s_up["p50"], 1), "p99": rnd(s_up["p99"], 1), "err": rnd(s_up["err"], 4),
        "app": rnd(s_up["app"], 3), "app_idle": rnd(s_pre["app"], 3), "cpu": rnd(s_up["cpu"], 3),
        "psys": rnd(s_up["psys"], 2), "psys_idle": rnd(s_pre["psys"], 2),
        "mj_per_req": rnd((s_up["psys"] - s_pre["psys"]) / s_up["achieved"] * 1000, 1) if s_up["achieved"] else None,
        "lock_per_s": rnd(counter_in("dotnet.monitor.lock_contentions", up[0] + 60, up[1], "mean"), 1),
        "db_pending_max": rnd(counter_in("db.client.connection.npgsql.pending_requests", up[0] + 60, up[1], "max"), 0),
    }

    # ---------------------------------------------------------------- phase-resolved latency against the broadcast cycle (C2)
    P = okpos[(okpos.t >= up[0] + 30) & (okpos.t < up[1])]
    start = (P.t - P.rt).values
    idx = np.searchsorted(at_sorted, start, side="right") - 1
    keep = idx >= 0
    dd = start[keep] - at_sorted[idx[keep]]
    lat = P.rt_ms.values[keep]
    m = dd < 1.5 * 1.2
    dd, lat = dd[m], lat[m]
    bins = np.arange(0, 1.8001, 0.05)
    which = np.digitize(dd, bins) - 1
    q = lambda p_: [float(np.percentile(lat[which == i], p_)) if (which == i).sum() >= 5 else None for i in range(len(bins) - 1)]
    p50 = q(50)
    floor = min(v for v in p50 if v is not None)
    B = next((float(bins[i]) for i, v in enumerate(p50) if v is not None and v <= 3 * floor + 5), None)
    rng = np.random.default_rng(1)
    sel = rng.choice(len(dd), size=min(3000, len(dd)), replace=False)
    period = float(np.median(np.diff(at_sorted[(at_sorted >= up[0]) & (at_sorted < up[1])])))
    out["sync"] = {"d": rnd(dd[sel], 3), "lat": rnd(lat[sel], 1), "bins": rnd(bins[:-1] + 0.025, 3), "p50": rnd(p50, 1), "p90": rnd(q(90), 1),
                   "B": rnd(B, 2), "floor": rnd(floor, 1), "n": int(len(dd)), "period": rnd(period, 3)}

    # ---------------------------------------------------------------- spectrum of the latency (C2, 20 Hz grid of the mean latency)
    fs = 20.0
    a, b = up[0] + 30, up[1]
    g = np.floor((P.t.values - a) * fs).astype(int)
    n = int((b - a) * fs)
    sums = np.bincount(g[(g >= 0) & (g < n)], weights=P.rt_ms.values[(g >= 0) & (g < n)], minlength=n)
    cnts = np.bincount(g[(g >= 0) & (g < n)], minlength=n)
    x = pd.Series(np.where(cnts > 0, sums / np.maximum(cnts, 1), np.nan)).interpolate(limit_direction="both").values
    f_, Pxx = signal.welch(x - x.mean(), fs, nperseg=min(1024, len(x)), detrend="linear")
    keep = (f_ > 0.05) & (f_ <= 5)
    out["psd"] = {"f": rnd(f_[keep], 4), "db": rnd(10 * np.log10(Pxx[keep] + 1e-9), 2), "f_bc": rnd(1 / period, 4),
                  "lines": [rnd(k / period, 4) for k in range(1, 7) if k / period <= 5]}
    # line strength: power at the broadcast harmonics over the median floor
    floor_db = float(np.median(10 * np.log10(Pxx[keep] + 1e-9)))
    peak = []
    for k in range(1, 4):
        w = (f_ > k / period - 0.03) & (f_ < k / period + 0.03)
        peak.append(float(10 * np.log10(Pxx[w].max() + 1e-9)) - floor_db if w.any() else None)
    out["psd"]["line_db"] = rnd(peak, 1)

    # ---------------------------------------------------------------- mass start (C3): 3 impulses averaged
    traces, rows = [], []
    bins_i = np.arange(-5, 60.01, 0.25)
    for r in ph.itertuples():
        if not r.phase.startswith("C3 impulse"):
            continue
        ti = r.t0 + 30
        Pi = pos[(pos.t >= ti - 5) & (pos.t < ti + 60)]
        okc, _ = np.histogram(Pi[Pi.status == 202].t - ti, bins_i)
        # server latency of what completed in each bin (p95)
        okp = Pi[Pi.status == 202]
        wb = np.digitize(okp.t - ti, bins_i) - 1
        lat95 = [float(okp.rt_ms.values[wb == i].max()) if (wb == i).sum() else np.nan for i in range(len(bins_i) - 1)]
        burst = req[(req.kind == "load") & (req.t >= ti - 0.5) & (req.t < ti + 1.5)]
        # warm: the share of the burst's phones that sent within the token cache lifetime (10 min) before it
        seen = req[(req.status == 202) & (req.t < ti - 0.5)].groupby("runner").t.max().reindex(burst.runner.unique())
        warm = float(((ti - seen) <= 600).mean())
        traces.append({"ok": okc / 0.25, "lat": lat95, "warm": warm})
        good = burst[burst.status == 202]
        rows.append({"warm": rnd(warm, 2), "sent": int(len(burst)), "ok": int(len(good)), "err": int(len(burst) - len(good)),
                     "c_p50": rnd(good.latency_us.median() / 1000, 0), "c_p99": rnd(good.latency_us.quantile(0.99) / 1000, 0),
                     "c_max": rnd(good.latency_us.max() / 1000, 0),
                     "drain_s": rnd((good.t + good.latency_us / 1e6).max() - ti, 2) if len(good) else None})
    # the chart shows the warmest impulse (a real mass start: the phones sent positions before the gun); the cold ones stay in rows
    w = int(np.argmax([t_["warm"] for t_ in traces]))
    out["impulse"] = {"t": rnd(bins_i[:-1] + 0.125, 3), "rows": rows, "warm_index": w,
                      "ok": rnd(traces[w]["ok"], 0), "lat_max": rnd(traces[w]["lat"], 0),
                      "drain_s": rows[w]["drain_s"], "c_p99": rows[w]["c_p99"], "c_p50": rows[w]["c_p50"], "warm": rows[w]["warm"],
                      "cold_c_p99": rnd(np.mean([r_["c_p99"] for i, r_ in enumerate(rows) if i != w]), 0),
                      "err": int(sum(r_["err"] for r_ in rows)), "sent": int(sum(r_["sent"] for r_ in rows))}
    return out


if __name__ == "__main__":
    bc, bs, ac, as_, path = sys.argv[1:6]
    R = {"before": run(bc, bs), "after": run(ac, as_), "generated": datetime.now(timezone.utc).isoformat(timespec="seconds"),
         "setup": {"registered": 1500, "laps": 10, "lap_km": 5.67, "cadence_s": 5, "build_before": "68fceb1", "build_after": "78bdfbf"}}
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(R, fh, ensure_ascii=False, separators=(",", ":"))
    for k in ("before", "after"):
        r = R[k]
        print(k, "capacity", r["capacity_rps"], "limited", r["capacity_limited"], "| C2", r["same_load"], "| B", r["sync"]["B"],
              "| lines", r["psd"]["line_db"], "| impulse warm", r["impulse"]["warm"], "drain", r["impulse"]["drain_s"], "p99", r["impulse"]["c_p99"],
              "cold p99", r["impulse"]["cold_c_p99"], "| db pending stairs", r["db_pending_stairs"], "at", r["db_pending_at"])
