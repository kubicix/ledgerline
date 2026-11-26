#!/usr/bin/env bash
# Proves the Grafana dashboard is provisioned and that the services export the series it reads.
# Starts the Compose stack, runs the smoke script, then asks Grafana (not Prometheus directly) for:
#   1. exactly one dashboard matching "Ledgerline", and
#   2. data for the outbox lag histogram.
# Exit 0 on success, 1 on any failure. The stack is stopped at the end (volumes are never removed).
#
# LEDGERLINE_DASHBOARD_PROJECT  Compose project name. Default: ledgerline-m3-dashboard-<timestamp>, a fresh project
#                               per run, because smoke.sh posts with fixed idempotency keys and asserts exact balances
#                               and a reused project keeps its volumes. Override only with a name never used before.
# LEDGERLINE_DASHBOARD_KEEP=1   leave the stack running afterwards.
# LEDGERLINE_DASHBOARD_TIMEOUT  seconds to wait for each check (default 180).
# Only one Ledgerline Compose project can run at a time: compose.yaml publishes fixed host ports.
set -euo pipefail

cd "$(dirname "$0")/.."

project="${LEDGERLINE_DASHBOARD_PROJECT:-ledgerline-m3-dashboard-$(date +%Y%m%d%H%M%S)}"
keep="${LEDGERLINE_DASHBOARD_KEEP:-0}"
timeout="${LEDGERLINE_DASHBOARD_TIMEOUT:-180}"
grafana_url="${GRAFANA_URL:-http://localhost:3000}"
# Prometheus name of the ledgerline.outbox.lag histogram (unit s): dots become underscores, unit "s" becomes
# "_seconds", the histogram count series gets "_count". Verified against the running stack.
lag_count_series='ledgerline_outbox_lag_seconds_count'

compose() { docker compose -p "$project" --profile app "$@"; }
fail() { echo "CHECK FAILED: $*" >&2; exit 1; }

cleanup() {
  local code=$?
  if [[ "$keep" == 1 ]]; then
    echo "Leaving project $project running (LEDGERLINE_DASHBOARD_KEEP=1)."
  else
    compose down >/dev/null 2>&1 || echo "WARNING: could not stop project $project" >&2
  fi
  exit "$code"
}
trap cleanup EXIT

# Retries a command until it succeeds or the timeout passes. The metrics path is asynchronous:
# the OTLP export interval and the Prometheus ingestion both add delay after the smoke run.
wait_for() {
  local description="$1" deadline=$((SECONDS + timeout))
  shift
  until "$@"; do
    if [[ $SECONDS -ge $deadline ]]; then fail "$description (waited ${timeout}s)"; fi
    sleep 3
  done
  echo "ok: $description"
}

dashboard_count() {
  curl -fsS -G "$grafana_url/api/search" --data-urlencode 'query=Ledgerline' | grep -o '"uid":"' | wc -l | tr -d ' '
}
exactly_one_dashboard() { [[ "$(dashboard_count)" == 1 ]]; }

lag_has_data() {
  local body
  body="$(curl -fsS -G "$grafana_url/api/datasources/proxy/uid/prometheus/api/v1/query" \
    --data-urlencode "query=$lag_count_series > 0")"
  # A non-empty result vector means at least one series satisfies the comparison.
  [[ "$body" == *'"result":[{'* ]]
}

compose up -d --build --wait
./scripts/smoke.sh
wait_for 'Grafana lists exactly one dashboard matching "Ledgerline"' exactly_one_dashboard
wait_for "Prometheus (through Grafana) has $lag_count_series > 0" lag_has_data
echo 'DASHBOARD OK'
