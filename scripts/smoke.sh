#!/usr/bin/env bash
set -euo pipefail

gateway_url="${GATEWAY_URL:-http://localhost:8080}"
accounts_url="$gateway_url/api"
payments_url="$gateway_url/api"
keycloak_url="${KEYCLOAK_URL:-http://localhost:8081/realms/ledgerline}"
mailpit_url="${MAILPIT_URL:-http://localhost:8025}"
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
wait_payment() {
  local id="$1" owner="$2" expected="$3" status deadline=$((SECONDS + 30))
  while true; do
    status="$(get "$payments_url/payments/$id" "$owner" | json_string status)"
    if [[ "$status" == "$expected" ]]; then return; fi
    if [[ "$expected" == Authorized && "$status" == Captured ]]; then return; fi
    if [[ "$status" == Failed || "$status" == Rejected || "$status" == ManualReview || "$status" == Compensated || $SECONDS -ge $deadline ]]; then echo "Payment $id ended or timed out: $status" >&2; return 1; fi
    sleep 0.2
  done
}

alice="$(token alice "${ALICE_PASSWORD:-alice_dev}")"
bob="$(token bob "${BOB_PASSWORD:-bob_dev}")"
shop="$(token shop "${SHOP_PASSWORD:-shop_dev}")"
admin="$(token admin "${ADMIN_PASSWORD:-admin_dev}")"
alice_account="$(post "$accounts_url/accounts" "$alice" smoke-alice-wallet-try-v1 '{"type":"CustomerWallet","currency":"TRY"}' | json_string id)"
bob_account="$(post "$accounts_url/accounts" "$bob" smoke-bob-wallet-try-v1 '{"type":"CustomerWallet","currency":"TRY"}' | json_string id)"
shop_account="$(post "$accounts_url/accounts" "$shop" smoke-shop-wallet-try-v1 '{"type":"MerchantWallet","currency":"TRY"}' | json_string id)"
topup="$(post "$payments_url/topups" "$alice" smoke-topup-v1 "{\"accountId\":\"$alice_account\",\"amountMinor\":10000,\"currency\":\"TRY\",\"cardToken\":\"tok_ok\"}" | json_string id)"
wait_payment "$topup" "$alice" Completed
transfer="$(post "$payments_url/transfers" "$alice" smoke-transfer-v1 "{\"fromAccountId\":\"$alice_account\",\"toAccountId\":\"$bob_account\",\"amountMinor\":2500,\"currency\":\"TRY\"}" | json_string id)"
wait_payment "$transfer" "$alice" Completed
merchant="$(post "$payments_url/payments" "$alice" smoke-merchant-v2 "{\"customerAccountId\":\"$alice_account\",\"merchantAccountId\":\"$shop_account\",\"amountMinor\":3000,\"currency\":\"TRY\",\"captureMode\":\"Manual\"}" | json_string id)"
wait_payment "$merchant" "$alice" Authorized
post "$payments_url/payments/$merchant/capture" "$shop" smoke-capture-v2 '{"amountMinor":2000}' >/dev/null
wait_payment "$merchant" "$alice" Captured
refund="$(post "$payments_url/payments/$merchant/refunds" "$shop" smoke-refund-v2 '{"amountMinor":500}' | json_string id)"
deadline=$((SECONDS + 30))
while true; do
  refunds="$(get "$payments_url/payments/$merchant/refunds" "$shop")"
  if [[ "$(echo "$refunds" | json_string id)" == "$refund" && "$(echo "$refunds" | json_string status)" == Completed ]]; then break; fi
  if [[ $SECONDS -ge $deadline ]]; then echo 'Refund did not complete' >&2; exit 1; fi
  sleep 0.2
done
alice_balance="$(get "$accounts_url/accounts/$alice_account" "$alice" | json_number balanceMinor)"
bob_balance="$(get "$accounts_url/accounts/$bob_account" "$bob" | json_number balanceMinor)"
shop_balance="$(get "$accounts_url/accounts/$shop_account" "$shop" | json_number balanceMinor)"
if [[ "$alice_balance" != 6000 || "$bob_balance" != 2500 || "$shop_balance" != 1500 ]]; then echo "Balance mismatch: alice=$alice_balance bob=$bob_balance shop=$shop_balance" >&2; exit 1; fi
trial="$(get "$accounts_url/ledger/trial-balance" "$admin")"
if echo "$trial" | grep -Eq '"sumOfBalancesMinor":-?[1-9][0-9]*'; then echo 'Trial balance is not zero' >&2; exit 1; fi
echo "$trial" | grep -q '"sumOfBalancesMinor":0'
deadline=$((SECONDS + 30))
while [[ "$(curl --fail-with-body -sS "$mailpit_url/api/v1/messages" | json_number total)" -lt 4 ]]; do
  if [[ $SECONDS -ge $deadline ]]; then echo 'Mailpit did not receive at least four emails' >&2; exit 1; fi
  sleep 0.2
done
echo 'SMOKE OK: alice=6000 bob=2500 shop=1500 trial=0 emails>=4'
