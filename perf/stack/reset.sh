#!/bin/sh
# Resets the load-test stack to a fresh database with the seeded live event and restarts the host samplers.
# Run on the home server from ~/pt-loadtest (sudo is needed for the sampler: RAPL counters are root-only).
set -e
cd "$(dirname "$0")"
sudo -n pkill -f '^python3 sampler.py' || true
pkill -f '^python3 jitter.py' || true
docker compose down -v
sudo -n rm -rf data
mkdir -p data/nginx data/counters data/pg data/host
docker compose up -d db app
wait_rebuilt() {
  for i in $(seq 1 60); do
    docker logs --since "$1" pt-lt-app 2>&1 | grep -q Rebuilt && return 0
    sleep 2
  done
  echo "app did not start" >&2; exit 1
}
wait_rebuilt 10m
docker exec -i pt-lt-db psql -q -U loadtest -d positiontracker -v ON_ERROR_STOP=1 < seed.sql
since=$(date +%s)
docker restart pt-lt-app >/dev/null
wait_rebuilt "$since"
docker logs pt-lt-app 2>&1 | grep Rebuilt | tail -1
docker compose up -d
sudo -n setsid nohup python3 sampler.py data/host pt-lt-app pt-lt-db pt-lt-nginx > data/host/sampler.out 2>&1 < /dev/null &
setsid nohup python3 jitter.py data/host > data/host/jitter.out 2>&1 < /dev/null &
sleep 3
tail -1 data/host/events.log
