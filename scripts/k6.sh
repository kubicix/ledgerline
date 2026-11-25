#!/usr/bin/env bash
# Runs a k6 scenario from tests/k6 in grafana/k6:2.4.0 on the Compose network of the stack under test and writes the
# end-of-test summary to docs/results/raw/<scenario>-<yyyy-mm-dd>.json.
# Usage: scripts/k6.sh <scenario> [extra k6 run arguments]
#   LEDGERLINE_PROJECT  Compose project name of the stack (default ledgerline); the network is <project>_default.
#   K6_NETWORK          Docker network to join instead.
#   Passed through when set: GATEWAY_URL, KEYCLOAK_URL, DURATION, RUN_ID, K6_CUSTOMERS, K6_MERCHANTS, K6_ADMIN,
#   SAGA_TIMEOUT_MS (can only raise the saga poll timeout, see tests/k6/lib/saga.js), and ALICE_PASSWORD,
#   BOB_PASSWORD, SHOP_PASSWORD and ADMIN_PASSWORD. The load-test users always use their <username>_dev default.
set -euo pipefail

scenario="${1:?usage: scripts/k6.sh <scenario> [k6 run arguments]}"
shift
cd "$(dirname "$0")/.."
if [[ ! -f "tests/k6/$scenario.js" ]]; then echo "Unknown scenario: tests/k6/$scenario.js" >&2; exit 2; fi

# Git Bash: pwd -W gives a Windows path Docker Desktop can mount; MSYS_NO_PATHCONV stops argument rewriting.
root="$(pwd -W 2>/dev/null || pwd)"
export MSYS_NO_PATHCONV=1
network="${K6_NETWORK:-${LEDGERLINE_PROJECT:-ledgerline}_default}"
summary="$scenario-$(date -u +%Y-%m-%d).json"
mkdir -p docs/results/raw

env_args=()
for name in GATEWAY_URL KEYCLOAK_URL DURATION RUN_ID K6_CUSTOMERS K6_MERCHANTS K6_ADMIN \
  SAGA_TIMEOUT_MS ALICE_PASSWORD BOB_PASSWORD SHOP_PASSWORD ADMIN_PASSWORD; do
  if [[ -n "${!name:-}" ]]; then env_args+=(-e "$name"); fi
done
# On Linux the container user must be able to write the bind-mounted results directory.
user_args=()
if [[ "$(uname -s)" == Linux ]]; then user_args=(--user "$(id -u):$(id -g)"); fi

docker run --rm --network "$network" "${user_args[@]}" "${env_args[@]}" \
  -v "$root/tests/k6:/scripts:ro" -v "$root/docs/results/raw:/results" \
  grafana/k6:2.4.0 run --summary-export "/results/$summary" "$@" "/scripts/$scenario.js"
