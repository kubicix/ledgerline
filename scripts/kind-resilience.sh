#!/usr/bin/env bash
# Resilience proof on the kind cluster "ledgerline" (M4-T07, ADR 0011): deleting pods and a rolling restart during a
# workload lose no money. Run it after scripts/kind-up.sh and scripts/kind-deploy.sh; it can be run again on the same
# cluster (docs/kubernetes.md, "Resilience").
#
# Run 1, pod deletion: Payments at 1 replica (the chart default; redeployed when it is not). tests/k6/smoke.js runs for
# 60 s; 15 s into its load phase every Payments pod is force-deleted, at 30 s every Accounts pod.
# Run 2, rolling restart: Payments redeployed with --set services.payments.replicas=2; smoke.js runs again and
# `kubectl rollout restart deployment/payments` is issued 10 s into its load phase.
# After each run, through the API: no payment of the workload's wallets left Processing or Compensating, none in
# ManualReview, k6's teardown saw every customer wallet drained, and the trial balance is zero in every currency.
# Run 1 also: a saga completed after the last pod deletion. Run 2 also: every failed gateway request of the load phase
# is a 503 inside the restart window, and gateway requests were recorded both before the restart and during the
# rollout. Fault times come from the Docker engine clock, the clock of k6's CSV timestamps.
#
# k6 runs in Docker through scripts/k6.sh on the kind node's Docker network and reaches the gateway and Keycloak at the
# node's NodePorts. The checks of this script use the host mappings, as scripts/smoke.sh does:
#   GATEWAY_URL   default http://localhost:8080
#   KEYCLOAK_URL  default http://localhost:8081/realms/ledgerline
# Prints RESILIENCE OK and exits 0 on success; exits non-zero with the reason otherwise.
set -euo pipefail

cd "$(dirname "$0")/.."
cluster=ledgerline
context="kind-$cluster"
gateway_url="${GATEWAY_URL:-http://localhost:8080}"
keycloak_url="${KEYCLOAK_URL:-http://localhost:8081/realms/ledgerline}"
kc=(kubectl --context "$context")
# Seconds into the load phase (plan schedule for run 1; run 2 restarts early so the rollout overlaps the load).
delete_payments_at=15
delete_accounts_at=30
restart_at=10
# A 503 counts as inside the restart window until this long after `rollout status` returned: the gateway's active
# health check of the payments cluster runs every 10 s with a 3 s timeout, so a destination marked unhealthy during
# the rollout can answer 503 for one more interval (src/Gateway appsettings).
restart_window_tail_s=15
work="$(mktemp -d)"
csv_files=()
k6_pid=""
started=$SECONDS

stamp() { date -u +%H:%M:%S; }
say() { echo "kind-resilience.sh: $(stamp) $*"; }
fail() { echo "kind-resilience.sh: $(stamp) FAILED: $*" >&2; exit 1; }
cleanup() {
  if [[ -n "$k6_pid" ]] && kill -0 "$k6_pid" 2>/dev/null; then
    echo "kind-resilience.sh: waiting for k6 to end before cleaning up" >&2
    wait "$k6_pid" 2>/dev/null || true
  fi
  if ((${#csv_files[@]} > 0)); then rm -f "${csv_files[@]}"; fi
  rm -rf "$work"
}
trap cleanup EXIT

json_string() { sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p"; }
token() {
  curl --fail-with-body -sS "$keycloak_url/protocol/openid-connect/token" \
    --data-urlencode 'grant_type=password' --data-urlencode 'client_id=ledgerline-k6' \
    --data-urlencode "username=$1" --data-urlencode "password=$2" | json_string access_token
}
# GET through the gateway. Retries briefly on 429 and 5xx, so a rate limit or a gateway health mark that has not caught
# up yet is never read as an empty answer.
api_get() {
  local code deadline=$((SECONDS + 30))
  while true; do
    code="$(curl -sS -o "$work/response" -w '%{http_code}' "$1" -H "Authorization: Bearer $2")" || code=000
    if [[ "$code" == 200 ]]; then cat "$work/response"; return; fi
    if [[ ! "$code" =~ ^(000|429|502|503|504)$ || $SECONDS -ge $deadline ]]; then
      echo "GET $1 failed: $code" >&2; cat "$work/response" >&2; echo >&2; return 1
    fi
    sleep 1
  done
}
node_time() { docker exec "$node" date +%s; }
wait_ready() {
  local name
  for name in "$@"; do "${kc[@]}" rollout status "deployment/$name" --timeout=300s >/dev/null || fail "deployment/$name is not Ready"; done
}
deploy() {
  say "kind-deploy.sh $*"
  local log="$work/deploy.log"
  if ! ./scripts/kind-deploy.sh "$@" >"$log" 2>&1; then tail -n 40 "$log" >&2; fail "kind-deploy.sh $* failed"; fi
  wait_ready accounts payments risk notifications banksim gateway
}

# ---- k6 workload -------------------------------------------------------------------------------------------------
# k6.sh writes its summary to docs/results/raw/<scenario>-<date>.json; a later --summary-export wins, so the summary
# stays inside the container. The per-request CSV must be read back, so it goes to the mounted results directory under
# a name of its own and is deleted on exit.
start_workload() {
  local name="$1"
  k6_log="$work/$name.log"
  local csv_name="kind-resilience-$name-$$.csv"
  k6_csv="docs/results/raw/$csv_name"
  csv_files+=("$k6_csv")
  # Pin the scenario to smoke.js's defaults (60 s, the default user pool) and a fresh RUN_ID (new idempotency keys).
  env -u K6_CUSTOMERS -u K6_MERCHANTS -u K6_ADMIN -u DURATION -u RUN_ID -u SAGA_TIMEOUT_MS \
    K6_NETWORK="$network" GATEWAY_URL="$k6_gateway" KEYCLOAK_URL="$k6_keycloak" \
    ./scripts/k6.sh smoke --summary-export /tmp/summary.json --out "csv=/results/$csv_name" >"$k6_log" 2>&1 &
  k6_pid=$!
  workload_start=$SECONDS
  say "$name: k6 smoke.js started"
}
# k6 prints "running (<elapsed>), <n>/<max> VUs, ..." once a second from the start of the scenario; during setup()
# it prints only "setup()". Sets load_start and vus.
wait_load_phase() {
  local deadline=$((SECONDS + 660)) line
  until line="$(grep -m1 '^running (' "$k6_log")"; do
    if ! kill -0 "$k6_pid" 2>/dev/null; then tail -n 40 "$k6_log" >&2; fail "k6 ended before its load phase started"; fi
    if ((SECONDS >= deadline)); then fail "k6 setup did not finish within 660 s"; fi
    sleep 0.5
  done
  load_start=$SECONDS
  vus="$(echo "$line" | sed -n 's/.*, [0-9]*\/\([0-9]*\) VUs.*/\1/p')"
  [[ "$vus" =~ ^[1-9][0-9]*$ ]] || fail "cannot read the VU count from: $line"
  say "load phase started ($vus VUs) after $((SECONDS - workload_start)) s of setup"
}
at_load_second() {
  local wait=$((load_start + $1 - SECONDS))
  if ((wait > 0)); then sleep "$wait"; fi
}
force_delete() {
  local out
  say "force-deleting pods of $1 ($((SECONDS - load_start)) s into the load phase)"
  out="$("${kc[@]}" delete pod -l "app.kubernetes.io/name=$1" --grace-period=0 --force 2>/dev/null)" || true
  echo "$out"
  grep -q 'deleted' <<<"$out" || fail "no $1 pod was deleted"
  last_deletion="$(node_time)"
  say "$1 pods deleted at $last_deletion (Docker engine clock)"
}
# k6 exit 99 means a threshold was crossed. The thresholds include latency targets this host misses for reasons outside
# Ledgerline (disk stalls, M3), so 99 is accepted and the money is judged by this script's own checks. Any other
# non-zero code (a setup or teardown error, an aborted run) fails the run.
finish_workload() {
  local name="$1" code
  set +e; wait "$k6_pid"; code=$?; set -e
  k6_pid=""
  sed -n '/THRESHOLDS/,$p' "$k6_log"
  if [[ "$code" != 0 && "$code" != 99 ]]; then tail -n 40 "$k6_log" >&2; fail "$name: k6 exited with $code"; fi
  grep -q 'teardown: backlog drained' "$k6_log" || fail "$name: k6 teardown did not see every customer wallet drained"
  k6_exit="$code"
  say "$name: k6 ended with exit $code after $((SECONDS - workload_start)) s"
}

# ---- k6 CSV ------------------------------------------------------------------------------------------------------
# Every check takes the CSV file as its first argument, so it can also be run on a saved file (source this script).
# Sets the variable named $3 to the 1-based index of column $2 in the header of CSV file $1. A missing or doubled
# column fails the run: an empty index would make awk read the whole row ($0) and the check pass.
csv_column() {
  local index
  index="$(head -n 1 "$1" | tr -d '\r' | tr ',' '\n' | grep -nx "$2" | cut -d: -f1)" || true
  [[ "$index" =~ ^[1-9][0-9]*$ ]] || fail "the k6 CSV has no '$2' column, or has it twice: $1"
  printf -v "$3" '%s' "$index"
}
# Quoted fields are removed first, so a comma inside a quoted k6 error text cannot shift the columns.
csv_rows() { sed 's/"[^"]*"//g' "$1" | tr -d '\r' | awk -F, -v metric="$2" '$1 == metric'; }
csv_count() { csv_rows "$1" "$2" | wc -l | tr -d ' '; }
# Fails unless $2 is a time of the Docker engine clock (whole seconds since the epoch); $1 names it.
require_time() { [[ "$2" =~ ^[1-9][0-9]*$ ]] || fail "$1 is not a Docker engine time: '$2'"; }
workload_counts() {
  completed="$(csv_count "$2" saga_completed)"
  unsuccessful="$(csv_count "$2" saga_unsuccessful)"
  timeouts="$(csv_count "$2" saga_timeouts)"
  not_accepted="$(csv_count "$2" accept_failed)"
  say "$1: sagas completed=$completed unsuccessful=$unsuccessful timeouts=$timeouts not-accepted=$not_accepted"
  ((completed > 0)) || fail "$1: no saga completed, the workload did not run"
}
# Run 1: at least one saga completed after the last pod deletion ($2), so the deletions hit a running workload. k6
# timestamps and $2 are whole seconds of the Docker engine clock, so the comparison is strict.
check_saga_after_deletion() {
  local csv="$1" deleted_at="$2" ts after
  require_time "run1: the last pod deletion" "$deleted_at"
  csv_column "$csv" timestamp ts
  after="$(csv_rows "$csv" saga_completed |
    awk -F, -v ts="$ts" -v at="$deleted_at" '$ts ~ /^[0-9]+$/ && $ts + 0 > at + 0 { n++ } END { print n + 0 }')"
  [[ "$after" =~ ^[0-9]+$ ]] || fail "run1: cannot count the sagas completed after the last pod deletion"
  say "run1: $after sagas completed after the last pod deletion"
  ((after > 0)) || fail "run1: no saga completed after the last pod deletion"
}
# Run 2, over the http_reqs rows of the load phase (scenario smoke). The restart window runs from `rollout restart`
# ($2) until restart_window_tail_s after `rollout status` returned ($3). Timestamps are whole seconds of the Docker
# engine clock, so the rollout itself counts only the seconds strictly between $2 and $3.
# - Every row has expected_response true or false and a numeric timestamp and status; anything else fails the run.
# - A gateway request failed when expected_response is false or its status is 0 (no answer) or 400 and above. Every
#   failed one must be a 503 inside the restart window.
# - At least one gateway request lies before the restart and one inside the rollout, so the restart hit live traffic.
# Gateway requests are the ones whose url tag contains /api/: k6 sets the url tag to the name tag that lib/api.js gives
# every gateway request ("GET /api/payments/{id}"), and the Keycloak token requests have no /api/ in either.
check_restart_failures() {
  local csv="$1" from="$2" until="$3" ts st sc ex url count
  require_time "run2: the start of the restart window" "$from"
  require_time "run2: the end of the rollout" "$until"
  csv_column "$csv" timestamp ts
  csv_column "$csv" status st
  csv_column "$csv" scenario sc
  csv_column "$csv" expected_response ex
  csv_column "$csv" url url
  csv_rows "$csv" http_reqs | awk -F, -v ts="$ts" -v st="$st" -v sc="$sc" -v ex="$ex" -v url="$url" -v gw="/api/" \
    -v from="$from" -v until="$until" -v to="$((until + restart_window_tail_s))" '
    $sc != "smoke" { next }
    ($ex != "true" && $ex != "false") || $ts !~ /^[0-9]+$/ || $st !~ /^[0-9]+$/ {
      unreadable++
      if (unreadable <= 20) printf "  unreadable request: t=%s status=%s expected_response=%s %s\n", $ts, $st, $ex, $url
      next
    }
    index($url, gw) == 0 { next }
    { total++; t = $ts + 0; s = $st + 0 }
    t < from + 0 { before++ }
    t > from + 0 && t < until + 0 { during++ }
    $ex == "false" || s == 0 || s >= 400 {
      if (s == 503 && t >= from + 0 && t <= to + 0) allowed++
      else { bad++; if (bad <= 20) printf "  failed request: t=%s status=%s %s\n", $ts, $st, $url }
    }
    END { printf "%d %d %d %d %d %d\n", total, before, during, allowed, bad, unreadable }' >"$work/failures"
  sed '$d' "$work/failures"
  read -r gateway_requests requests_before requests_during allowed_503 other_failures unreadable \
    < <(tail -n 1 "$work/failures")
  for count in gateway_requests requests_before requests_during allowed_503 other_failures unreadable; do
    [[ "${!count}" =~ ^[0-9]+$ ]] || fail "run2: cannot read the request counts from the k6 CSV"
  done
  say "run2: $gateway_requests gateway requests in the load phase ($requests_before before the restart," \
    "$requests_during during the rollout), $allowed_503 answered 503 in the restart window," \
    "$other_failures other failures, $unreadable unreadable rows"
  ((unreadable == 0)) ||
    fail "run2: $unreadable load-phase requests with an expected_response other than true or false, or no numeric status or timestamp"
  ((gateway_requests > 0)) || fail "run2: no gateway request was recorded"
  ((other_failures == 0)) || fail "run2: $other_failures failed gateway requests other than 503 in the restart window"
  ((requests_before > 0)) || fail "run2: no gateway request before the restart"
  ((requests_during > 0)) || fail "run2: no gateway request during the rollout"
}

# ---- money checks through the API --------------------------------------------------------------------------------
# The payments a smoke.js load phase creates all touch the wallets of its VU users: VU n acts for load-customer-00n
# (lib/setup.js walletOfVu), its top-ups credit that wallet and its transfers debit it. GET /api/payments filters by
# account (source or destination), so the VU wallets enumerate every load-phase payment. Setup top-ups of the other
# wallets are awaited to Completed by setup() itself, which fails the k6 run otherwise.
assert_money() {
  local name="$1" admin i user owner account wallets=() wallet status pending deadline trial
  admin="$(token admin "${ADMIN_PASSWORD:-admin_dev}")" || fail "$name: no admin token"
  for ((i = 1; i <= vus; i++)); do
    user="$(printf 'load-customer-%03d' "$i")"
    owner="$(token "$user" "${user}_dev")" || fail "$name: no token for $user"
    account="$(api_get "$gateway_url/api/accounts" "$owner" |
      grep -o '"id":"[^"]*","ownerId":"[^"]*","type":"CustomerWallet","currency":"TRY"' | json_string id)"
    [[ -n "$account" ]] || fail "$name: $user has no TRY customer wallet"
    wallets+=("$account")
  done
  deadline=$((SECONDS + 180))
  while true; do
    pending=0
    for wallet in "${wallets[@]}"; do
      for status in Processing Compensating; do
        if ! api_get "$gateway_url/api/payments?accountId=$wallet&status=$status&limit=1" "$admin" | grep -q '"items":\[\]'; then
          pending=$((pending + 1))
        fi
      done
    done
    if ((pending == 0)); then break; fi
    if ((SECONDS >= deadline)); then fail "$name: payments still Processing or Compensating after 180 s"; fi
    sleep 2
  done
  for wallet in "${wallets[@]}"; do
    api_get "$gateway_url/api/payments?accountId=$wallet&status=ManualReview&limit=100" "$admin" >"$work/manual"
    if ! grep -q '"items":\[\]' "$work/manual"; then
      grep -o '"id":"[^"]*","kind":"[^"]*","status":"ManualReview"' "$work/manual" >&2 || true
      fail "$name: payments in ManualReview"
    fi
  done
  trial="$(api_get "$gateway_url/api/ledger/trial-balance" "$admin")"
  if echo "$trial" | grep -Eq '"sumOfBalancesMinor":-?[1-9][0-9]*'; then echo "$trial" >&2; fail "$name: trial balance is not zero"; fi
  echo "$trial" | grep -q '"sumOfBalancesMinor":0' || { echo "$trial" >&2; fail "$name: trial balance has no currency"; }
  say "$name: ${#wallets[@]} VU wallets: no payment Processing or Compensating, none in ManualReview; trial balance: $trial"
}

# Sourced, the script stops here with its functions defined, so the k6 CSV checks can be run on a saved file.
[[ "${BASH_SOURCE[0]}" == "$0" ]] || return 0

# ---- preflight ---------------------------------------------------------------------------------------------------
for tool in docker kind helm kubectl curl; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool not found on PATH, see docs/kubernetes.md" >&2; exit 1; }
done
kind get clusters 2>/dev/null | grep -qx "$cluster" || fail "kind cluster '$cluster' does not exist, run scripts/kind-up.sh and scripts/kind-deploy.sh"
node="$(kind get nodes --name "$cluster" | head -n 1)"
network="$(docker inspect -f '{{range $name, $value := .NetworkSettings.Networks}}{{$name}} {{end}}' "$node" | awk '{print $1}')"
gateway_port="$("${kc[@]}" get service gateway -o jsonpath='{.spec.ports[0].nodePort}')"
keycloak_port="$("${kc[@]}" get service keycloak -o jsonpath='{.spec.ports[0].nodePort}')"
[[ -n "$node" && -n "$network" && -n "$gateway_port" && -n "$keycloak_port" ]] || fail "cannot read the kind node, its network or the NodePorts"
k6_gateway="http://$node:$gateway_port"
k6_keycloak="http://$node:$keycloak_port/realms/ledgerline"
say "k6 joins Docker network '$network' and uses $k6_gateway and $k6_keycloak"

# ---- run 1: pod deletion -----------------------------------------------------------------------------------------
phase_start=$SECONDS
if [[ "$("${kc[@]}" get deployment payments -o jsonpath='{.spec.replicas}')" != 1 ]]; then deploy; fi
wait_ready accounts payments risk notifications banksim gateway
start_workload run1
wait_load_phase
at_load_second "$delete_payments_at"
force_delete payments
at_load_second "$delete_accounts_at"
force_delete accounts
finish_workload run1
wait_ready payments accounts
workload_counts run1 "$k6_csv"
check_saga_after_deletion "$k6_csv" "$last_deletion"
run1_completed="$completed"
assert_money run1
run1_seconds=$((SECONDS - phase_start))

# ---- run 2: rolling restart with two Payments replicas ---------------------------------------------------------------
phase_start=$SECONDS
deploy --set services.payments.replicas=2
ready="$("${kc[@]}" get deployment payments -o jsonpath='{.status.readyReplicas}')"
[[ "$ready" == 2 ]] || fail "payments has $ready ready replicas, expected 2"
start_workload run2
wait_load_phase
at_load_second "$restart_at"
window_start="$(node_time)"
say "rollout restart deployment/payments ($((SECONDS - load_start)) s into the load phase)"
"${kc[@]}" rollout restart deployment/payments
"${kc[@]}" rollout status deployment/payments --timeout=300s
window_end="$(node_time)"
rollout_seconds=$((window_end - window_start))
say "rollout finished after $rollout_seconds s"
finish_workload run2
wait_ready payments
workload_counts run2 "$k6_csv"
run2_completed="$completed"
check_restart_failures "$k6_csv" "$window_start" "$window_end"
assert_money run2
run2_seconds=$((SECONDS - phase_start))

echo "RESILIENCE OK: run1 pod deletion: sagas completed=$run1_completed, ${run1_seconds}s;" \
  "run2 rolling restart (2 replicas, rollout ${rollout_seconds}s): sagas completed=$run2_completed," \
  "503 in window=$allowed_503, other failed requests=0, ${run2_seconds}s;" \
  "no payment Processing, ManualReview=0, trial balance=0; total $((SECONDS - started))s"
