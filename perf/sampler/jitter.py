#!/usr/bin/env python3
"""Scheduler wake-up latency, cyclictest style: a 1 kHz loop on an absolute schedule records how late each wake-up is.

One line per second: count, mean, p50, p90, p99, p99.9, max (us) and a log2 histogram (bins 1,2,4,...,65536 us).
A user-space Python loop, so the absolute numbers include interpreter overhead; the change under load is the signal.

usage: jitter.py <outdir>
"""
import os, sys, time

OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)
PERIOD = 0.001
BINS = 18

with open(os.path.join(OUT, "jitter.csv"), "w", buffering=1) as f:
    f.write("t,count,mean_us,p50_us,p90_us,p99_us,p999_us,max_us," + ",".join(f"h{2**i}" for i in range(BINS)) + "\n")
    nxt = time.monotonic() + PERIOD
    window_end = time.time() + 1
    lates = []
    while True:
        d = nxt - time.monotonic()
        if d > 0:
            time.sleep(d)
        late = (time.monotonic() - nxt) * 1e6
        lates.append(late)
        nxt += PERIOD
        if late > 50_000:  # suspended or starved: resynchronise instead of a burst of catch-up wake-ups
            nxt = time.monotonic() + PERIOD
        if time.time() >= window_end:
            lates.sort()
            n = len(lates)
            hist = [0] * BINS
            for v in lates:
                b = 0 if v < 1 else min(BINS - 1, int(v).bit_length() - 1)
                hist[b] += 1
            q = lambda p: lates[min(n - 1, int(p * n))]
            f.write(f"{time.time():.3f},{n},{sum(lates) / n:.1f},{q(.5):.1f},{q(.9):.1f},{q(.99):.1f},{q(.999):.1f},{lates[-1]:.1f},"
                    + ",".join(map(str, hist)) + "\n")
            lates = []
            window_end += 1
