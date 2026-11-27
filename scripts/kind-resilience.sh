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
# Run 2 also: every failed gateway request of the load phase is a 503 inside the restart window.
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
# Quoted fields are removed first, so a comma inside a quoted k6 error text cannot shift the columns.
csv_column() { head -n 1 "$k6_csv" | tr -d '\r' | tr ',' '\n' | grep -nx "$1" | cut -d: -f1; }
csv_rows() { sed 's/"[^"]*"//g' "$k6_csv" | tr -d '\r' | awk -F, -v metric="$1" '$1 == metric'; }
csv_count() { csv_rows "$1" | wc -l | tr -d ' '; }
workload_counts() {
  completed="$(csv_count saga_completed)"
  unsuccessful="$(csv_count saga_unsuccessful)"
  timeouts="$(csv_count saga_timeouts)"
  not_accepted="$(csv_count accept_failed)"
  say "$1: sagas completed=$completed unsuccessful=$unsuccessful timeouts=$timeouts not-accepted=$not_accepted"
  ((completed > 0)) || fail "$1: no saga completed, the workload did not run"
}
# Every http_reqs row of the load phase (scenario smoke) to the gateway with expected_response=false must be a 503
# whose k6 timestamp lies inside the restart window. Timestamps are whole seconds of the Docker engine clock. Gateway
# requests are the ones whose url tag contains /api/: k6 sets the url tag to the name tag that lib/api.js gives every
# gateway request ("GET /api/payments/{id}"), and the Keycloak token requests have no /api/ in either.
check_restart_failures() {
  local ts status scenario expected url
  ts="$(csv_column timestamp)" status="$(csv_column status)" scenario="$(csv_column scenario)"
  expected="$(csv_column expected_response)" url="$(csv_column url)"
  csv_rows http_reqs | awk -F, -v ts="$ts" -v st="$status" -v sc="$scenario" -v ex="$expected" -v url="$url" \
    -v gw="/api/" -v from="$window_start" -v to="$((window_end + restart_window_tail_s))" '
    $sc == "smoke" && index($url, gw) > 0 { total++ }
    $sc == "smoke" && index($url, gw) > 0 && $ex == "false" {
      if ($st == "503" && $ts >= from && $ts <= to) allowed++
      else { bad++; if (bad <= 20) printf "  failed request: t=%s status=%s %s\n", $ts, $st, $url }
    }
    END { printf "%d %d %d\n", total, allowed, bad }' >"$work/failures"
  sed '$d' "$work/failures"
  read -r gateway_requests allowed_503 other_failures < <(tail -n 1 "$work/failures")
  say "run2: $gateway_requests gateway requests in the load phase, $allowed_503 answered 503 in the restart window, $other_failures other failures"
  ((gateway_requests > 0)) || fail "run2: no gateway request was recorded"
  ((other_failures == 0)) || fail "run2: $other_failures failed gateway requests other than 503 in the restart window"
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
workload_counts run1
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
workload_counts run2
run2_completed="$completed"
check_restart_failures
assert_money run2
run2_seconds=$((SECONDS - phase_start))

echo "RESILIENCE OK: run1 pod deletion: sagas completed=$run1_completed, ${run1_seconds}s;" \
  "run2 rolling restart (2 replicas, rollout ${rollout_seconds}s): sagas completed=$run2_completed," \
  "503 in window=$allowed_503, other failed requests=0, ${run2_seconds}s;" \
  "no payment Processing, ManualReview=0, trial balance=0; total $((SECONDS - started))s"
