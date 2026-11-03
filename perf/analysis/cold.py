#!/usr/bin/env python3
"""The coldest mass start (CMP,C0,C3: 1 000 phones the server has never seen) without and with the token warm-up -> JSON.

usage: cold.py <withoutClient> <withoutServer> <withClient> <withServer> <out.json>

Only the first impulse is used: its phones sent nothing before, so every token is unknown to the cache unless it was loaded
in advance. Client latency of the burst (requests intended within 2 s of the impulse), errors and the Npgsql queue peak.
"""
import json, sys
import pandas as pd


def run(client, server):
    req = pd.read_csv(f"{client}/requests.csv")
    ph = pd.read_csv(f"{client}/phases.csv")
    cnt = pd.read_csv(f"{server}/counters/counters.csv")
    first = ph[ph.phase == "C3 impulse 1"].iloc[0]
    ti = first.start_us + 30e6
    burst = req[(req.kind == "load") & (req.intended_us >= ti - 0.5e6) & (req.intended_us < ti + 1.5e6)]
    seen = req[(req.status == 202) & (req.intended_us < ti - 0.5e6)].runner.unique()
    good = burst[burst.status == 202]
    pend = cnt[cnt["Counter Name"].str.startswith("db.client.connection.npgsql.pending_requests")]["Mean/Increment"]
    return {"sent": int(len(burst)), "err": int(len(burst) - len(good)), "seen_before": int(burst.runner.isin(seen).sum()),
            "p50": round(float(good.latency_us.median()) / 1000), "p99": round(float(good.latency_us.quantile(0.99)) / 1000),
            "max": round(float(good.latency_us.max()) / 1000), "db_pending_max": int(pend.max()) if len(pend) else 0}


if __name__ == "__main__":
    a, b, c, d, out = sys.argv[1:6]
    R = {"without": run(a, b), "with": run(c, d)}
    json.dump(R, open(out, "w", encoding="utf-8"), ensure_ascii=False)
    print(R)
