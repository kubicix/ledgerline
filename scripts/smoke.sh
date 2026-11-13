#!/usr/bin/env bash
set -euo pipefail

accounts_url="${ACCOUNTS_URL:-http://localhost:5101}"
payments_url="${PAYMENTS_URL:-http://localhost:5102}"
response_file="$(mktemp)"
trap 'rm -f "$response_file"' EXIT

new_id() {
  local raw
  raw="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
  printf '%s-%s-4%s-8%s-%s' "${raw:0:8}" "${raw:8:4}" "${raw:13:3}" "${raw:17:3}" "${raw:20:12}"
}
json_string() { sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p"; }
json_number() { sed -n "s/.*\"$1\":\([-0-9]*\).*/\1/p"; }

post() {
  local url="$1" owner="$2" key="$3" body="$4" code deadline=$((SECONDS + 30))
  while true; do
    code="$(curl -sS -o "$response_file" -w '%{http_code}' "$url" -H "X-Dev-User: $owner" -H 'X-Dev-Roles: customer' \
      -H "Idempotency-Key: $key" -H 'Content-Type: application/json' --data "$body")"
    if [[ "$code" == 201 || "$code" == 202 ]]; then cat "$response_file"; return; fi
    if [[ "$code" != 404 || $SECONDS -ge $deadline ]]; then cat "$response_file" >&2; echo "POST failed: $code" >&2; return 1; fi
    sleep 0.2
  done
}
get() { curl --fail-with-body -sS "$1" -H "X-Dev-User: $2" -H 'X-Dev-Roles: customer'; }
wait_completed() {
  local id="$1" owner="$2" status deadline=$((SECONDS + 30))
  while true; do
    status="$(get "$payments_url/payments/$id" "$owner" | json_string status)"
    if [[ "$status" == Completed ]]; then return; fi
    if [[ "$status" != Processing || $SECONDS -ge $deadline ]]; then echo "Payment $id ended or timed out: $status" >&2; return 1; fi
    sleep 0.2
  done
}

alice="$(new_id)"
bob="$(new_id)"
alice_account="$(post "$accounts_url/accounts" "$alice" open '{"type":"CustomerWallet","currency":"TRY"}' | json_string id)"
bob_account="$(post "$accounts_url/accounts" "$bob" open '{"type":"CustomerWallet","currency":"TRY"}' | json_string id)"
topup="$(post "$payments_url/topups" "$alice" topup "{\"accountId\":\"$alice_account\",\"amountMinor\":10000,\"currency\":\"TRY\",\"cardToken\":\"tok_ok\"}" | json_string id)"
wait_completed "$topup" "$alice"
transfer="$(post "$payments_url/transfers" "$alice" transfer "{\"fromAccountId\":\"$alice_account\",\"toAccountId\":\"$bob_account\",\"amountMinor\":2500,\"currency\":\"TRY\"}" | json_string id)"
wait_completed "$transfer" "$alice"
alice_balance="$(get "$accounts_url/accounts/$alice_account" "$alice" | json_number balanceMinor)"
bob_balance="$(get "$accounts_url/accounts/$bob_account" "$bob" | json_number balanceMinor)"
if [[ "$alice_balance" != 7500 || "$bob_balance" != 2500 ]]; then echo "Balance mismatch: alice=$alice_balance bob=$bob_balance" >&2; exit 1; fi
echo "SMOKE OK: alice=7500 bob=2500"
