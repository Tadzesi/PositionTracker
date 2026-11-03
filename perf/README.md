# Load and performance test on the home server

A measurement campaign of PositionTracker on the Intel NUC (Celeron N4505), published as a static report at
`https://www.lab463.com/tracker/perf/`.

| Path | What |
|---|---|
| `tools/LoadLab/` | .NET load generator: seeding SQL, open-loop scheduler, SignalR organizer/spectators, probe phones, WiFi ping |
| `perf/stack/` | throwaway stack for the server (`~/pt-loadtest`): app image, own Postgres, timing nginx, dotnet-counters and pg_stat sidecars, `reset.sh` |
| `perf/sampler/` | host DAQ: `sampler.py` (10 Hz, RAPL, temps, PSI, cgroups, DNS kill switch), `jitter.py` (1 kHz wake-up latency) |
| `perf/analysis/analyze.py` | raw CSVs → report JSON (clock alignment, Welch PSD, spectrogram, lock-in transfer functions, phase-resolved latency, fits) |
| `perf/report/` | page template, chart library, renderer, texts `body.sk.html` / `body.en.html`, translations `i18n/`, `build.py` → `site/perf/` |
| `perf/data/` | raw data and tokens (git-ignored) |

## Run

```sh
# 1. seed: confirmed runners with device tokens for a fresh database (published report: run 1 = 1500, run 2 = 400)
dotnet run -c Release --project tools/LoadLab -- seed site/assets/example-trat.gpx 400 perf/data/seedB
# 2. server: copy perf/stack/* perf/sampler/*.py perf/data/seedB/seed.sql to ~/pt-loadtest, write API_KEY=... to .env
ssh tadzesi@192.168.1.2 '~/pt-loadtest/reset.sh'     # fresh DB, seeded live event, samplers started
# 3. campaign (~2 h; timeScale 0.05 for a smoke run). Start it as a detached process: Claude Code reaps background shells
#    when the PC runs low on memory.
dotnet run -c Release --project tools/LoadLab -- run http://192.168.1.2:18080 perf/data/seedB <API_KEY> site/assets/example-trat.gpx perf/data/run2
# 4. collect, analyse, build
scp -r tadzesi@192.168.1.2:pt-loadtest/data/* perf/data/run2-server/
python perf/analysis/analyze.py perf/data/run2 perf/data/run2-server perf/data/report_run2.json
python perf/analysis/analyze.py perf/data/run1 perf/data/run1-server perf/data/report_run1.json
python perf/report/build.py perf/data/report_run2.json site/perf perf/data/report_run1.json
# deploy: copy site/perf/ (index.sk.html, index.en.html, assets/, *.gz) to ~/server/stacks/web/site/tracker/perf/; upload assets
#   first, then the pages, then delete assets no page references. nginx: deploy/home-server/nginx-positiontracker.conf
# 5. clean up the server
ssh tadzesi@192.168.1.2 'cd ~/pt-loadtest && sudo pkill -f "^python3 sampler.py"; pkill -f "^python3 jitter.py"; docker compose down -v'
```

The sampler runs as root (RAPL energy counters are root-only) and stops `pt-lt-nginx` when the home DNS slows down
(6 probes in a row over 250 ms) or the CPU passes 98 °C.

## Before/after comparison (section 00b of the report)

The first campaign seeded 400 laps, above the app's limit of 10, which made the ETA (and the lock it ran under) ~40x more
expensive than in a real race. The comparison runs the `CMP` plan on a 10-lap, 1 500-runner seed against two images:

```sh
dotnet run -c Release --project tools/LoadLab -- seed site/assets/example-trat.gpx 1500 perf/data/seedC 10
# server: git archive <commit> | build -> pt-lt-app:before (68fceb1) and pt-lt-app:after (78bdfbf), VITE_BASE=/tracker/app/
Start-Process powershell -ArgumentList '-File perf/compare.ps1' -WindowStyle Hidden   # ~1.5 h, collects and tears down
python perf/analysis/compare.py perf/data/cmp-before perf/data/cmp-before-server perf/data/cmp-after perf/data/cmp-after-server perf/data/compare.json
python perf/report/build.py perf/data/report_run2.json site/perf perf/data/report_run1.json perf/data/compare.json
```

The coldest mass start (1 000 phones the server has never seen) without and with the token warm-up:

```sh
Start-Process powershell -ArgumentList '-File','perf/compare.ps1','-Versions','after,warm','-Plan','CMP,C0,C3','-Tag','cold' -WindowStyle Hidden
python perf/analysis/cold.py perf/data/cold-after perf/data/cold-after-server perf/data/cold-warm perf/data/cold-warm-server perf/data/cold.json
python perf/report/build.py perf/data/report_run2.json site/perf perf/data/report_run1.json perf/data/compare.json perf/data/cold.json
```

## Languages and loading

The report exists in Slovak and English. `build.py` writes one small page per language (`index.sk.html`, `index.en.html`) and
shared, content-hashed assets: `app.<hash>.js` (charts + renderer), `core.<hash>.json` (summary, comparison, every value the text
binds to) and `data.<hash>.json` (long time series), each with a `.gz` for nginx `gzip_static`. The first screen needs the page,
the script and `core` (~70 kB gzip); `data` loads in parallel and charts are drawn when their section comes near the viewport.

- `/tracker/perf/` serves Slovak when the `pt_lang` cookie (the page's SK | EN switch) says so or, without it, when the browser's
  first language is Slovak; English otherwise. `/tracker/perf/sk/` and `/en/` are fixed addresses for sharing.
- Texts: `body.sk.html` / `body.en.html` (same ids and bindings, checked by the build), page chrome in `i18n/page.json`, strings of
  the renderer in `i18n/en.json` keyed by the Slovak text (`t('…')` in `report.js`). The build fails on a missing translation;
  `PT_DRAFT=1` turns that into warnings while translating.
