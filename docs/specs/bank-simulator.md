# Spec: Bank Simulator (test double)

Project `src/Services/BankSimulator/Ledgerline.BankSimulator`. Database `banksim_db`.

This is **not** part of the system under design. It is a fake card acquirer that lives
in the repository so the system has an external party that can decline, fail, time
out, and disagree at reconciliation. It shares no contracts or messaging code with the
services (enforced by an architecture test). It models no real provider's API.

## 1. Behavior

- Authorize, capture, void, and refund against opaque test card tokens.
- Idempotent by `Idempotency-Key`: the same key returns the same stored response.
- Look up the outcome of an operation by its idempotency key.
- Produce a settlement file per day.
- Inject faults on demand.

Authentication: header `X-Api-Key` must equal configuration `Bank:ApiKey`.
`401` otherwise.

## 2. Card tokens

| Token | Result of authorize |
|---|---|
| `tok_ok` | Authorized |
| `tok_declined` | Declined, `declineCode = "do_not_honor"` |
| `tok_insufficient` | Declined, `declineCode = "insufficient_funds"` |
| `tok_capture_fails` | Authorized; a later capture is declined with `capture_declined` |
| anything else | `400` |

## 3. API

| Method and path | Request | Success response |
|---|---|---|
| `POST /authorizations` | `{ "amountMinor", "currency", "cardToken", "reference" }` | `201 { "authorizationId", "status": "Authorized" }` or `402 { "status": "Declined", "declineCode" }` |
| `POST /authorizations/{id}/capture` | none | `200 { "captureId", "status": "Captured", "capturedAt" }` or `402 { "status": "Declined", "declineCode" }` |
| `POST /authorizations/{id}/void` | none | `200 { "status": "Voided" }` |
| `POST /captures/{id}/refund` | none (full refund only) | `200 { "refundId", "status": "Refunded", "refundedAt" }` |
| `GET /operations/{idempotencyKey}` | | `200 { "kind": "Authorize|Capture|Void|Refund", "httpStatus", "body" }` or `404` |
| `GET /settlements/{date}` | `date` as `yyyy-MM-dd` (UTC) | `200 text/csv` |
| `PUT /faults` | fault definition (below) | `204` |
| `DELETE /faults` | | `204` |
| `PUT /settlement-tampering` | `{ "date", "dropCaptureId"?, "alterCaptureId"?, "alteredAmountMinor"? }` | `204` |

All POSTs require `Idempotency-Key`. `reference` is the caller's payment ID and is
echoed into the settlement file.

State rules: capture requires `Authorized`; void requires `Authorized`; refund
requires `Captured`. A violation gives `409`. Repeating a call with the same
idempotency key returns the stored response and does not re-check state.

### Fault definition

```json
{ "operation": "Authorize|Capture|Void|Refund|Lookup|Any",
  "mode": "Error500|Timeout|SuccessThenTimeout|Delay",
  "count": 3,
  "delayMs": 8000 }
```

| Mode | Effect |
|---|---|
| `Error500` | Returns `500` without performing the operation |
| `Timeout` | Holds the connection for `delayMs` (default 30 000) without performing the operation |
| `SuccessThenTimeout` | Performs and stores the operation, then holds the connection. The caller sees a timeout although the bank did it |
| `Delay` | Performs the operation after `delayMs` and answers normally |

A fault applies to the next `count` matching requests, then expires. Faults are kept
in memory.

### Settlement file

One line per capture and per refund whose timestamp falls on the date (UTC):

```csv
record_type,bank_id,reference,amount_minor,currency,occurred_at
CAPTURE,cap_0193a7f2,0193a7f2-5a00-7000-8000-000000000001,150000,TRY,2026-11-13T10:15:31Z
REFUND,ref_0193a800,0193a7f2-5a00-7000-8000-000000000009,150000,TRY,2026-11-13T14:02:10Z
```

Tampering (for reconciliation tests) drops or alters rows in the generated file only.
The stored operations are not changed.

## 4. Database schema (`banksim_db`)

```sql
CREATE TABLE bank_operations (
    idempotency_key  text PRIMARY KEY,
    kind             text        NOT NULL,
    authorization_id text        NULL,
    capture_id       text        NULL,
    refund_id        text        NULL,
    reference        text        NULL,
    amount_minor     bigint      NULL,
    currency         char(3)     NULL,
    http_status      int         NOT NULL,
    response_body    jsonb       NOT NULL,
    occurred_at      timestamptz NOT NULL
);

CREATE TABLE bank_authorizations (
    id           text PRIMARY KEY,
    reference    text        NOT NULL,
    amount_minor bigint      NOT NULL,
    currency     char(3)     NOT NULL,
    card_token   text        NOT NULL,
    status       text        NOT NULL,          -- Authorized | Captured | Voided | Refunded
    capture_id   text        NULL,
    created_at   timestamptz NOT NULL
);
```

## 5. Invariants

| # | Invariant |
|---|---|
| B1 | One idempotency key performs at most one operation and always returns the same response |
| B2 | `SuccessThenTimeout` always leaves a record that `GET /operations/{key}` returns |
| B3 | The untampered settlement file contains exactly the stored captures and refunds of the day |

## 6. Honesty note for the README

Real acquirers differ in every detail: asynchronous webhooks, partial captures,
settlement formats, cut-off times, chargebacks. The simulator exists to exercise the
saga's failure handling, not to imitate a provider.
