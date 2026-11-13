# Spec: Reconciliation (Phase 2)

Project `src/Services/Reconciliation/Ledgerline.Reconciliation` (created in M5).
Database `reconciliation_db`. Quartz.NET 4.7.3 (ADR 0010). Consumes Kafka, calls Bank
Simulator over HTTP, publishes to RabbitMQ exchange `ledgerline.reconciliation`.

## 1. Responsibilities

- Once per day, compare three independent records of external money movement for the
  previous UTC day:
  1. **Bank:** the settlement file from Bank Simulator.
  2. **Payments:** completed and compensated top-ups (`payment-completed`,
     `payment-failed` with `status = Compensated`).
  3. **Ledger:** journal lines on `BANK_CLEARING`.
- Verify the trial balance is zero.
- Record mismatches, publish the result, and let Payments stamp reconciled payments.

It never fixes anything. It reports. Corrections are a human decision.

## 2. Inputs

Kafka consumer group `reconciliation` on `ledgerline.payments.events.v1` and
`ledgerline.ledger.entries.v1` fills two staging tables continuously (idempotent
upserts by message ID). The bank file is fetched by the job with
`GET /settlements/{date}`.

## 3. Job

Quartz job `DailyReconciliationJob`, cron `0 30 0 * * ?` (00:30 UTC), for business
date = yesterday. Misfire policy: fire once now. Clustered ADO.NET job store.

Also startable on demand: `POST /runs { "businessDate": "2026-11-13" }` (admin).

Steps:

1. Acquire Redis lock `recon:<businessDate>` (TTL 10 min, renewed). Not acquired: exit.
2. Insert `reconciliation_runs (business_date)` with status `Running`. A unique
   violation with status `Completed` means the day is done: exit. With status
   `Running` or `Failed`: delete that run's items and continue (re-run).
3. Wait until both Kafka consumers have processed records with timestamps past the end
   of the business date (or 5 minutes, then proceed and flag `inputs_incomplete`).
4. Fetch and parse the settlement file. Parse errors fail the run.
5. Match by `reference` (payment ID):

| Check | Mismatch code |
|---|---|
| Bank `CAPTURE` with no ledger `TopUp` debit on `BANK_CLEARING` and no bank `REFUND` for the same reference | `BankCaptureNotInLedger` |
| Ledger `TopUp` with no bank `CAPTURE` | `LedgerEntryNotAtBank` |
| Amount or currency differs between bank and ledger | `AmountMismatch` |
| Payment `Completed` in Payments but no ledger entry, or the reverse | `PaymentLedgerMismatch` |
| Payment `Compensated` with a bank `CAPTURE` but no bank `REFUND` | `RefundMissingAtBank` |
| Bank `CAPTURE` and `REFUND` both present and no ledger entry | matched (compensated cleanly) |
| Bank row whose reference is not a known payment | `UnknownBankRecord` |

6. Trial balance: per currency, the sum of all line amounts (credits minus debits)
   seen up to the end of the business date must be zero. Otherwise `LedgerImbalance`.
7. Store items, set run `Completed` with counts, enqueue events (outbox). Commit.

## 4. Messages published

`reconciliation.event.reconciliation-completed.v1`

```json
{ "runId": "...", "businessDate": "2026-11-13", "matched": 412, "mismatched": 1,
  "inputsIncomplete": false, "completedAt": "2026-11-14T00:30:09Z" }
```

`reconciliation.event.payments-reconciled.v1` (batches of at most 500 IDs)

```json
{ "runId": "...", "businessDate": "2026-11-13", "paymentIds": ["...", "..."] }
```

`reconciliation.event.reconciliation-mismatch-detected.v1` (one per mismatch)

```json
{ "runId": "...", "businessDate": "2026-11-13", "code": "BankCaptureNotInLedger",
  "reference": "0193a7f2-5a00-7000-8000-000000000001",
  "bankAmountMinor": 150000, "ledgerAmountMinor": null, "currency": "TRY" }
```

## 5. HTTP API (admin)

Through the gateway under `/api/reconciliation/...`.

| Method and path | Result |
|---|---|
| `POST /runs` | `202`, starts or re-runs a date |
| `GET /runs?limit=&cursor=` | Runs, newest first |
| `GET /runs/{businessDate}` | Run with counts and status |
| `GET /runs/{businessDate}/items?code=` | Mismatch items |

## 6. Database schema (`reconciliation_db`)

```sql
CREATE TABLE staged_payments (
    payment_id     uuid PRIMARY KEY,
    kind           text        NOT NULL,
    status         text        NOT NULL,
    amount_minor   bigint      NOT NULL,
    currency       char(3)     NOT NULL,
    bank_capture_id text       NULL,
    occurred_at    timestamptz NOT NULL,
    last_message_id uuid       NOT NULL
);

CREATE TABLE staged_ledger_lines (
    journal_entry_id uuid   NOT NULL,
    line_no          int    NOT NULL,
    account_id       uuid   NOT NULL,
    is_bank_clearing boolean NOT NULL,
    entry_type       text   NOT NULL,
    direction        text   NOT NULL,
    amount_minor     bigint NOT NULL,
    currency         char(3) NOT NULL,
    payment_id       uuid   NOT NULL,
    posted_at        timestamptz NOT NULL,
    PRIMARY KEY (journal_entry_id, line_no)
);

CREATE TABLE reconciliation_runs (
    id                uuid PRIMARY KEY,
    business_date     date        NOT NULL UNIQUE,
    status            text        NOT NULL,      -- Running | Completed | Failed
    matched           int         NOT NULL DEFAULT 0,
    mismatched        int         NOT NULL DEFAULT 0,
    inputs_incomplete boolean     NOT NULL DEFAULT false,
    error             text        NULL,
    started_at        timestamptz NOT NULL,
    completed_at      timestamptz NULL
);

CREATE TABLE reconciliation_items (
    id                  uuid PRIMARY KEY,
    run_id              uuid    NOT NULL REFERENCES reconciliation_runs (id) ON DELETE CASCADE,
    code                text    NOT NULL,
    reference           text    NOT NULL,
    bank_amount_minor   bigint  NULL,
    ledger_amount_minor bigint  NULL,
    currency            char(3) NULL
);

-- plus outbox_messages, and the Quartz ADO.NET job store tables (prefix qrtz_)
```

`BANK_CLEARING` account IDs are computed with the same deterministic UUIDv5 rule as
in `accounts.md`, so the service needs no call to Accounts.

## 7. Invariants

| # | Invariant | Proven by |
|---|---|---|
| C1 | At most one `Completed` run per business date | Unique constraint; concurrent-start test |
| C2 | Re-running a date gives the same items (given the same inputs) | Re-run test |
| C3 | A clean day produces zero mismatches | Happy-path test |
| C4 | Each tampering mode of Bank Simulator produces exactly its mismatch code | One test per code |
| C5 | Reconciliation never writes to any other service's data | It has no credentials for them |

## 8. Failure handling

| Failure | Behavior |
|---|---|
| Bank unreachable | Run `Failed` with the error. Quartz retries the job every 10 minutes, up to 6 times |
| Kafka lag | `inputs_incomplete = true`; the run completes and can be re-run |
| Crash mid-run | The run stays `Running`. The next trigger or a manual start re-runs it (step 2) |
| Two replicas | Quartz clustering picks one; the Redis lock and the unique date are second and third guards |
