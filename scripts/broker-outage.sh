#!/usr/bin/env bash
# Runs tests/k6/broker-outage.js through scripts/k6.sh and stops the `rabbitmq` service of the Compose project for the
# scenario's outage window: OUTAGE_START_S seconds (default 75) after the scenario logs that its load phase starts,
# for OUTAGE_DURATION_S seconds (default 30). The broker is started again when the script exits early, too.
# Usage: scripts/broker-outage.sh [extra k6 run arguments]
#   LEDGERLINE_PROJECT  Compose project name of the stack (default ledgerline), as for scripts/k6.sh.
#   The k6 log goes to stdout; the summary goes to docs/results/raw/broker-outage-<yyyy-mm-dd>.json.
set -euo pipefail

cd "$(dirname "$0")/.."
project="${LEDGERLINE_PROJECT:-ledgerline}"
start_s="${OUTAGE_START_S:-75}"
duration_s="${OUTAGE_DURATION_S:-30}"
compose=(docker compose -p "$project" --profile app)
marker='broker-outage: load phase starts'
log="$(mktemp)"
stopped=0

stamp() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }
start_broker() {
  echo "broker-outage.sh: $(stamp) starting rabbitmq"
  "${compose[@]}" start --wait --wait-timeout 300 rabbitmq
  echo "broker-outage.sh: $(stamp) rabbitmq is healthy"
  stopped=0
}
cleanup() {
  if [[ "$stopped" == 1 ]]; then start_broker; fi
  if [[ -n "${tail_pid:-}" ]]; then kill "$tail_pid" 2>/dev/null || true; fi
  rm -f "$log"
}
trap cleanup EXIT

./scripts/k6.sh broker-outage --env "OUTAGE_START_S=$start_s" --env "OUTAGE_DURATION_S=$duration_s" "$@" >"$log" 2>&1 &
k6_pid=$!
# tail ends by itself once k6 has ended (--pid), after printing the last lines.
tail -f --pid="$k6_pid" "$log" &
tail_pid=$!

# Wait for the end of setup (60 to 70 s for logins, wallets and top-ups).
until grep -q "$marker" "$log"; do
  if ! kill -0 "$k6_pid" 2>/dev/null; then
    set +e; wait "$k6_pid"; code=$?; wait "$tail_pid"; set -e
    unset tail_pid
    echo "broker-outage.sh: k6 ended before the load phase started (exit $code)" >&2
    exit "$code"
  fi
  sleep 0.2
done

sleep "$start_s"
echo "broker-outage.sh: $(stamp) stopping rabbitmq for $duration_s s"
stopped=1
stop_issued=$SECONDS
"${compose[@]}" stop rabbitmq
echo "broker-outage.sh: $(stamp) rabbitmq is stopped"
# The window runs from the stop command, as the scenario's window runs from its planned start, so the time the
# graceful stop itself takes counts toward it.
remaining=$((duration_s - (SECONDS - stop_issued)))
if ((remaining > 0)); then sleep "$remaining"; fi
start_broker

set +e; wait "$k6_pid"; code=$?; wait "$tail_pid"; set -e
unset tail_pid
exit "$code"
