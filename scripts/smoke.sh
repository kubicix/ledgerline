#!/usr/bin/env bash
set -euo pipefail

accounts_url="${ACCOUNTS_URL:-http://localhost:5101}"
payments_url="${PAYMENTS_URL:-http://localhost:5102}"
keycloak_url="${KEYCLOAK_URL:-http://localhost:8081/realms/ledgerline}"
response_file="$(mktemp)"
trap 'rm -f "$response_file"' EXIT

json_string() { sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p"; }
json_number() { sed -n "s/.*\"$1\":\([-0-9]*\).*/\1/p"; }
token() {
  curl --fail-with-body -sS "$keycloak_url/protocol/openid-connect/token" \
    --data-urlencode 'grant_type=password' --data-urlencode 'client_id=ledgerline-k6' \
    --data-urlencode "username=$1" --data-urlencode "password=$2" | json_string access_token
}

post() {
  local url="$1" owner="$2" key="$3" body="$4" code deadline=$((SECONDS + 30))
  while true; do
    code="$(curl -sS -o "$response_file" -w '%{http_code}' "$url" -H "Authorization: Bearer $owner" \
      -H "Idempotency-Key: $key" -H 'Content-Type: application/json' --data "$body")"
    if [[ "$code" == 201 || "$code" == 202 ]]; then cat "$response_file"; return; fi
    if [[ "$code" != 404 || $SECONDS -ge $deadline ]]; then cat "$response_file" >&2; echo "POST failed: $code" >&2; return 1; fi
    sleep 0.2
  done
}
get() { curl --fail-with-body -sS "$1" -H "Authorization: Bearer $2"; }
wait_completed() {
  local id="$1" owner="$2" status deadline=$((SECONDS + 30))
  while true; do
    status="$(get "$payments_url/payments/$id" "$owner" | json_string status)"
    if [[ "$status" == Completed ]]; then return; fi
    if [[ "$status" != Processing || $SECONDS -ge $deadline ]]; then echo "Payment $id ended or timed out: $status" >&2; return 1; fi
    sleep 0.2
  done
}

alice="$(token alice "${ALICE_PASSWORD:-alice_dev}")"
bob="$(token bob "${BOB_PASSWORD:-bob_dev}")"
alice_account="$(post "$accounts_url/accounts" "$alice" smoke-alice-wallet-try-v1 '{"type":"CustomerWallet","currency":"TRY"}' | json_string id)"
bob_account="$(post "$accounts_url/accounts" "$bob" smoke-bob-wallet-try-v1 '{"type":"CustomerWallet","currency":"TRY"}' | json_string id)"
topup="$(post "$payments_url/topups" "$alice" smoke-topup-v1 "{\"accountId\":\"$alice_account\",\"amountMinor\":10000,\"currency\":\"TRY\",\"cardToken\":\"tok_ok\"}" | json_string id)"
wait_completed "$topup" "$alice"
transfer="$(post "$payments_url/transfers" "$alice" smoke-transfer-v1 "{\"fromAccountId\":\"$alice_account\",\"toAccountId\":\"$bob_account\",\"amountMinor\":2500,\"currency\":\"TRY\"}" | json_string id)"
wait_completed "$transfer" "$alice"
alice_balance="$(get "$accounts_url/accounts/$alice_account" "$alice" | json_number balanceMinor)"
bob_balance="$(get "$accounts_url/accounts/$bob_account" "$bob" | json_number balanceMinor)"
if [[ "$alice_balance" != 7500 || "$bob_balance" != 2500 ]]; then echo "Balance mismatch: alice=$alice_balance bob=$bob_balance" >&2; exit 1; fi
echo "SMOKE OK: alice=7500 bob=2500"
