# Spec: Payments

Project `src/Services/Payments/Ledgerline.Payments`. Database `payments_db`.
Decisions: ADR 0003, 0006. Shared conventions: `building-blocks.md`.

## 1. Responsibilities

- Accept payment requests: top-up, transfer, merchant payment, capture, void, refund.
- Orchestrate each payment as a saga across Risk, the bank, and Accounts.
- Compensate when a later step fails after an earlier step had an external effect.
- Own the payment record and its timeline. Publish payment events.

Not responsible for: balances (it never computes or stores one), risk rules, sending
notifications.

## 2. Domain

| Concept | Values |
|---|---|
| Kind | `TopUp`, `Transfer`, `MerchantPayment` |
| Status | `Processing`, `Authorized`, `Capturing`, `Captured`, `Voiding`, `Voided`, `Completed`, `Compensating`, `Compensated`, `Rejected`, `Failed`, `ManualReview` |
| Saga step | `None`, `RiskCheck`, `BankAuthorize`, `BankCapture`, `LedgerCredit`, `LedgerTransfer`, `LedgerReserve`, `LedgerCapture`, `LedgerRelease`, `BankVoid`, `BankRefund` |
| Refund status | `Processing`, `Completed`, `Failed` |
| Capture mode | `Manual`, `Automatic` |

Final statuses: `Completed`, `Voided`, `Compensated`, `Rejected`, `Failed`,
`ManualReview`. `Captured` is final for the saga but refunds may still be created.

`operationId` for a ledger command is deterministic:
UUIDv5(namespace = `paymentId`, name = step name), and for refunds
UUIDv5(namespace = `refundId`, name = `"RefundFunds"`). Retries reuse it by construction.

Bank idempotency keys: `"<paymentId>:authorize"`, `":capture"`, `":void"`, `":refund"`.

## 3. HTTP API

Through the gateway under `/api/payments/...`. All POSTs require `Idempotency-Key` and
return `202 Accepted` with `Location: /payments/{id}` and the payment body. Validation
and ownership are checked synchronously against the local `account_refs` replica.

### Payment representation

```json
{
  "id": "0193a7f2-5a00-7000-8000-000000000001",
  "kind": "MerchantPayment",
  "status": "Authorized",
  "amountMinor": 10000,
  "currency": "TRY",
  "sourceAccountId": "...",
  "destinationAccountId": "...",
  "captureMode": "Manual",
  "capturedMinor": 0,
  "refundedMinor": 0,
  "failureReason": null,
  "authorizationExpiresAt": "2026-11-20T10:15:31Z",
  "reconciledAt": null,
  "createdAt": "2026-11-13T10:15:30Z",
  "updatedAt": "2026-11-13T10:15:31Z",
  "timeline": [
    { "step": "RiskCheck", "outcome": "Succeeded", "at": "2026-11-13T10:15:30.400Z" },
    { "step": "LedgerReserve", "outcome": "Succeeded", "at": "2026-11-13T10:15:31.020Z" }
  ]
}
```

For `TopUp`, `sourceAccountId` is null and `destinationAccountId` is the wallet.

### Endpoints

| Method and path | Role | Body | Synchronous checks |
|---|---|---|---|
| `POST /topups` | customer | `{ "accountId", "amountMinor", "currency", "cardToken" }` | Caller owns `accountId`; it is a `CustomerWallet`, `Active`; currency matches |
| `POST /transfers` | customer | `{ "fromAccountId", "toAccountId", "amountMinor", "currency" }` | Caller owns `fromAccountId`; both exist and are `Active`; same currency; not the same account |
| `POST /payments` | customer | `{ "customerAccountId", "merchantAccountId", "amountMinor", "currency", "captureMode" }` | Caller owns the customer account; the merchant account is a `MerchantWallet`; same currency |
| `POST /payments/{id}/capture` | merchant | `{ "amountMinor" }` (optional, default: full) | Caller owns the merchant account; status `Authorized`; `0 < amount <= amountMinor` |
| `POST /payments/{id}/void` | merchant, admin | none | Status `Authorized` |
| `POST /payments/{id}/refunds` | merchant | `{ "amountMinor" }` | Status `Captured`; `refundedMinor + pending refunds + amount <= capturedMinor` |
| `GET /payments/{id}` | party or admin | | Caller owns the source or destination account |
| `GET /payments?accountId=&status=&limit=&cursor=` | party or admin | | Caller owns `accountId` |
| `GET /payments/{id}/refunds` | party or admin | | |

Error mapping: unknown or foreign account or payment gives `404 not-found`; a state
check failure gives `409 invalid-state`; other rule failures give
`400 validation-failed`.

`account_refs` is eventually consistent. An account opened a moment ago may not be
known yet and gives `404`. Clients retry; the UI waits for the account to appear.

`cardToken` is an opaque test token understood by Bank Simulator (`tok_ok`,
`tok_declined`, `tok_insufficient`). It is stored on the payment only until the bank
authorization completes, then cleared.

Refund representation:

```json
{ "id": "...", "paymentId": "...", "amountMinor": 2500, "status": "Completed",
  "failureReason": null, "createdAt": "2026-11-13T12:00:00Z" }
```

## 4. Sagas

Notation: `state --message--> next state / actions`. Every transition is one local
transaction that also writes a `saga_steps` row. Messages that do not match the
current step are acknowledged and ignored.

### 4.1 TopUp

| # | Current (status, step) | Trigger | Next | Actions |
|---|---|---|---|---|
| T1 | request accepted | | `Processing`, `RiskCheck` | send `evaluate-risk`; timeout 30 s |
| T2 | `Processing`, `RiskCheck` | `risk-approved` | `Processing`, `BankAuthorize` | send `call-bank(Authorize)` |
| T3 | `Processing`, `RiskCheck` | `risk-rejected` | `Rejected`, `None` | publish `payment-failed` |
| T4 | `Processing`, `BankAuthorize` | bank: authorized | `Processing`, `BankCapture` | store `bank_authorization_id`; clear card token; send `call-bank(Capture)` |
| T5 | `Processing`, `BankAuthorize` | bank: declined | `Failed`, `None` | reason `BankDeclined:<code>`; publish `payment-failed` |
| T6 | `Processing`, `BankCapture` | bank: captured | `Processing`, `LedgerCredit` | store `bank_capture_id`; send `credit-top-up`; timeout 30 s |
| T7 | `Processing`, `BankCapture` | bank: capture declined | `Compensating`, `BankVoid` | send `call-bank(Void)` |
| T8 | `Processing`, `LedgerCredit` | `ledger-operation-succeeded` | `Completed`, `None` | publish `payment-completed` |
| T9 | `Processing`, `LedgerCredit` | `ledger-operation-rejected` | `Compensating`, `BankRefund` | reason from reply; send `call-bank(Refund)` |
| T10 | `Compensating`, `BankVoid` or `BankRefund` | bank: done | `Compensated`, `None` | publish `payment-failed` with `status = Compensated` |

### 4.2 Transfer

| # | Current | Trigger | Next | Actions |
|---|---|---|---|---|
| X1 | request accepted | | `Processing`, `RiskCheck` | send `evaluate-risk` |
| X2 | `Processing`, `RiskCheck` | `risk-approved` | `Processing`, `LedgerTransfer` | send `transfer-funds` |
| X3 | `Processing`, `RiskCheck` | `risk-rejected` | `Rejected` | publish `payment-failed` |
| X4 | `Processing`, `LedgerTransfer` | succeeded | `Completed` | publish `payment-completed` |
| X5 | `Processing`, `LedgerTransfer` | rejected | `Failed` | publish `payment-failed` |

### 4.3 MerchantPayment

| # | Current | Trigger | Next | Actions |
|---|---|---|---|---|
| M1 | request accepted | | `Processing`, `RiskCheck` | send `evaluate-risk` |
| M2 | `Processing`, `RiskCheck` | `risk-approved` | `Processing`, `LedgerReserve` | send `reserve-funds` |
| M3 | `Processing`, `RiskCheck` | `risk-rejected` | `Rejected` | publish `payment-failed` |
| M4 | `Processing`, `LedgerReserve` | succeeded, mode `Manual` | `Authorized`, `None` | set `authorization_expires_at = now + 7 d`; expiry timeout; publish `payment-authorized` |
| M5 | `Processing`, `LedgerReserve` | succeeded, mode `Automatic` | `Capturing`, `LedgerCapture` | send `capture-funds` (full amount) |
| M6 | `Processing`, `LedgerReserve` | rejected | `Failed` | publish `payment-failed` |
| M7 | `Authorized` | `POST capture` | `Capturing`, `LedgerCapture` | store requested amount; send `capture-funds` |
| M8 | `Capturing`, `LedgerCapture` | succeeded | `Captured`, `None` | set `captured_minor`; publish `payment-captured` |
| M9 | `Capturing`, `LedgerCapture` | rejected | `Authorized`, `None` | set `failure_reason`; the merchant may retry or void |
| M10 | `Authorized` | `POST void` or expiry timeout | `Voiding`, `LedgerRelease` | send `release-funds` |
| M11 | `Voiding`, `LedgerRelease` | succeeded | `Voided`, `None` | publish `payment-voided` |
| M12 | `Voiding`, `LedgerRelease` | rejected | `ManualReview` | funds are stuck in `PAYMENTS_PENDING`; must be looked at |

M7 and M10 race (capture versus void or expiry). Both update the payment with an
optimistic concurrency check on `version` and the guard `status = Authorized`. Exactly
one wins; the loser gets `409 invalid-state` (or, for the expiry timer, does nothing).

### 4.4 Refund

A refund is its own small saga keyed by `refundId`.

| # | Current | Trigger | Next | Actions |
|---|---|---|---|---|
| R1 | request accepted | | refund `Processing` | send `refund-funds` |
| R2 | `Processing` | succeeded | `Completed` | `payments.refunded_minor += amount`; publish `payment-refunded` |
| R3 | `Processing` | rejected | `Failed` | store reason. The amount no longer counts against the refundable total |

### 4.5 Bank calls

The handler of `payments.command.call-bank.v1`:

1. Load the payment. If its step no longer matches the command's operation, stop.
2. Call the bank with the step's idempotency key. HTTP timeout 5 s, no automatic
   HTTP retries for POST.
3. Definitive answer (2xx with a result, or a 4xx decline): apply the transition.
4. No definitive answer (timeout, connection error, 5xx): call
   `GET /operations/{idempotencyKey}`.
   - Found: apply the transition for the recorded result.
   - `404`: the operation did not happen. Throw, so the message goes to the retry queue.
   - Lookup also fails: throw (retry).
5. When the consumer's retries are exhausted the message dead-letters and the step
   timeout takes over.

The transition in step 3 is a normal state change in the same transaction as the
inbox row. If the process dies after the bank answered but before commit, the
redelivered command repeats the call with the same idempotency key and the bank
returns the same result.

### 4.6 Timeouts and retries

| Step | Timeout | On timeout | Max attempts | After the last attempt |
|---|---|---|---|---|
| `RiskCheck` | 30 s | re-send the command (same `paymentId`) | 5 | `Failed`, reason `RiskUnavailable` (nothing to undo) |
| Ledger steps | 30 s | re-send (same `operationId`) | 5 | `ManualReview` |
| `BankAuthorize`, `BankCapture` | 15 s | re-send `call-bank` | 5 | `ManualReview` |
| `BankVoid`, `BankRefund` | `min(2^attempt s, 300 s)` | re-send `call-bank` | 8 | `ManualReview` |
| Authorization expiry | 7 days | transition M10 | 1 | |

`saga_timeouts` rows are deleted when their step completes. The timeout poller runs
every second and uses `FOR UPDATE SKIP LOCKED`.

Entering `ManualReview` increments `ledgerline.saga.manual_review` and publishes
`payment-failed` with `status = ManualReview`. Nothing moves automatically afterwards.

## 5. Messages

Exchange `ledgerline.payments`. Queue `payments.inbox`. Records in
`Ledgerline.Contracts.Payments`.

### Published events

Common fields on all: `paymentId`, `kind`, `customerId`, `amountMinor`, `currency`,
`sourceAccountId`, `destinationAccountId`, `occurredAt`.

| Type | Extra fields |
|---|---|
| `payments.event.payment-authorized.v1` | `authorizationExpiresAt` |
| `payments.event.payment-captured.v1` | `capturedMinor` |
| `payments.event.payment-completed.v1` | `bankCaptureId` (top-ups only, otherwise null) |
| `payments.event.payment-voided.v1` | `reason` (`Requested`, `Expired`) |
| `payments.event.payment-failed.v1` | `status` (`Rejected`, `Failed`, `Compensated`, `ManualReview`), `reasonCode` |
| `payments.event.payment-refunded.v1` | `refundId`, `refundAmountMinor`, `refundedTotalMinor` |

`customerId` is the owner of the customer-side account (destination for top-ups,
source otherwise).

### Published commands

- To Accounts: the six `accounts.command.*` types (`accounts.md`).
- To Risk: `risk.command.evaluate-risk.v1` (`risk.md`).
- To itself: `payments.command.call-bank.v1`
  `{ "paymentId", "operation": "Authorize|Capture|Void|Refund" }`.

### Consumed

| Type | Handling |
|---|---|
| `risk.event.risk-approved.v1`, `risk.event.risk-rejected.v1` | Saga transitions |
| `accounts.event.ledger-operation-succeeded.v1`, `...-rejected.v1` | Saga transitions, matched by `operationId` to a payment step or a refund |
| `accounts.event.account-opened.v1` | Upsert `account_refs` |
| `accounts.event.account-status-changed.v1` | Update `account_refs.status` |
| `payments.command.call-bank.v1` | Section 4.5 |
| `reconciliation.event.payments-reconciled.v1` (Phase 2) | Set `reconciled_at` |

## 6. Database schema (`payments_db`)

```sql
CREATE TABLE payments (
    id                       uuid PRIMARY KEY,
    kind                     text        NOT NULL,
    status                   text        NOT NULL,
    saga_step                text        NOT NULL,
    step_attempt             int         NOT NULL DEFAULT 1,
    customer_id              uuid        NOT NULL,
    source_account_id        uuid        NULL,
    destination_account_id   uuid        NOT NULL,
    amount_minor             bigint      NOT NULL CHECK (amount_minor > 0),
    currency                 char(3)     NOT NULL,
    capture_mode             text        NULL,
    capture_requested_minor  bigint      NULL,
    captured_minor           bigint      NOT NULL DEFAULT 0,
    refunded_minor           bigint      NOT NULL DEFAULT 0,
    card_token               text        NULL,
    bank_authorization_id    text        NULL,
    bank_capture_id          text        NULL,
    failure_reason           text        NULL,
    authorization_expires_at timestamptz NULL,
    reconciled_at            timestamptz NULL,
    idempotency_key          text        NOT NULL,
    request_hash             text        NOT NULL,
    created_at               timestamptz NOT NULL,
    updated_at               timestamptz NOT NULL,
    -- optimistic concurrency: EF Core row version mapped to PostgreSQL xmin
    CONSTRAINT uq_payment_idem UNIQUE (customer_id, idempotency_key),
    CONSTRAINT ck_captured CHECK (captured_minor <= amount_minor),
    CONSTRAINT ck_refunded CHECK (refunded_minor <= captured_minor)
);
CREATE INDEX ix_payments_source ON payments (source_account_id, created_at DESC);
CREATE INDEX ix_payments_destination ON payments (destination_account_id, created_at DESC);

CREATE TABLE refunds (
    id              uuid PRIMARY KEY,
    payment_id      uuid        NOT NULL REFERENCES payments (id),
    amount_minor    bigint      NOT NULL CHECK (amount_minor > 0),
    status          text        NOT NULL,
    failure_reason  text        NULL,
    requested_by    uuid        NOT NULL,
    idempotency_key text        NOT NULL,
    request_hash    text        NOT NULL,
    created_at      timestamptz NOT NULL,
    CONSTRAINT uq_refund_idem UNIQUE (requested_by, idempotency_key)
);

CREATE TABLE saga_steps (                        -- audit timeline, append-only
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    payment_id  uuid        NOT NULL REFERENCES payments (id),
    step        text        NOT NULL,
    outcome     text        NOT NULL,            -- 'Started' | 'Succeeded' | 'Rejected' | 'TimedOut' | 'Retried'
    detail      text        NULL,
    at          timestamptz NOT NULL
);
CREATE INDEX ix_saga_steps_payment ON saga_steps (payment_id, id);

CREATE TABLE saga_timeouts (
    id          uuid PRIMARY KEY,
    payment_id  uuid        NOT NULL REFERENCES payments (id),
    step        text        NOT NULL,
    due_at      timestamptz NOT NULL
);
CREATE INDEX ix_saga_timeouts_due ON saga_timeouts (due_at);

CREATE TABLE account_refs (                      -- replica of Accounts data
    account_id  uuid PRIMARY KEY,
    owner_id    uuid    NOT NULL,
    type        text    NOT NULL,
    currency    char(3) NOT NULL,
    status      text    NOT NULL
);

-- plus outbox_messages and inbox_messages from building-blocks.md
```

Capture, void, and refund POSTs use the same `Idempotency-Key` mechanism. For capture
and void the key and hash are stored in `saga_steps.detail` of the `Started` row and a
repeat with the same key returns the payment as it is now.

## 7. The saga core as a pure function

The transition tables are implemented once, without I/O, so they can be unit-tested
exhaustively:

```csharp
// Ledgerline.Payments.Sagas
public abstract record SagaInput;                // RiskApproved, RiskRejected, LedgerSucceeded, LedgerRejected(reason),
                                                 // BankAuthorized(id), BankDeclined(code), BankCaptured(id), BankCaptureDeclined,
                                                 // BankCompensated, CaptureRequested(amount), VoidRequested, AuthorizationExpired,
                                                 // StepTimedOut
public abstract record SagaEffect;               // SendCommand(object message), PublishEvent(object message),
                                                 // ScheduleTimeout(SagaStep step, TimeSpan after), CancelTimeouts
public sealed record SagaDecision(PaymentStatus Status, SagaStep Step, IReadOnlyList<SagaEffect> Effects,
                                  string? FailureReason, bool Ignored);

public static class PaymentSaga
{
    public static SagaDecision Decide(PaymentSnapshot payment, SagaInput input, DateTimeOffset now);
}
```

A thin application service loads the payment, calls `Decide`, applies the decision to
the row, writes `saga_steps`, and turns effects into outbox and timeout rows.

## 8. Invariants

| # | Invariant | Enforced by |
|---|---|---|
| P1 | A payment captures at most once | Guard `status = Authorized` plus `xmin` concurrency on M7; hold status in Accounts (A7) |
| P2 | `captured_minor <= amount_minor`, `refunded_minor <= captured_minor` | Synchronous checks and `CHECK` constraints |
| P3 | One request (`customer`, `Idempotency-Key`) creates at most one payment | Unique constraint |
| P4 | A wallet is credited for a top-up only after the bank captured | Step order T4, T6 |
| P5 | A bank capture without a ledger credit ends in a bank refund or `ManualReview`, never silently | T9, T10, timeouts |
| P6 | A final status never changes (except `Captured` gaining refunds) | `Decide` returns `Ignored` for final states |
| P7 | Every state change has a `saga_steps` row | Application service; integration test compares counts |
| P8 | No bank call happens inside a database transaction that decided it | Bank calls only in the `call-bank` handler |

## 9. Failure handling

| Failure | Behavior |
|---|---|
| Broker down | POSTs still return `202`; commands wait in the outbox. Timeouts do not fire falsely: a timeout re-sends through the outbox too, and attempts are only counted when the row is due |
| Payments crashes mid-saga | State is in the database. On restart the relay publishes pending outbox rows, the consumer resumes, and the timeout poller re-sends anything that got no reply |
| Risk down | Payment stays `Processing`. Re-sent every 30 s, 5 times, then `Failed (RiskUnavailable)` |
| Accounts down | Payment stays in its ledger step. Re-sent, then `ManualReview` |
| Bank down or slow | Section 4.5. Never assumes failure without a lookup |
| Duplicate reply | Inbox, then the step guard |
| Reply for a payment in a final state | Ignored, logged at information level |
| Reply with unknown `operationId` | Dead-lettered: it indicates a contract bug |

While the broker is down, the broker-down attempt counting rule matters: a step
attempt is consumed only when the re-sent command was confirmed by the relay
(`outbox_messages.processed_at` set). The poller checks this before incrementing
`step_attempt`, so an outage does not push healthy payments into `ManualReview`.
