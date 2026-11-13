# Spec: Accounts and Ledger

Project `src/Services/Accounts/Ledgerline.Accounts`. Database `accounts_db`.
Decisions: ADR 0003, 0007. Shared conventions: `building-blocks.md`.

## 1. Responsibilities

- Open accounts and manage their status.
- Be the only writer of money: post balanced journal entries, keep balances and holds.
- Keep the event store and its projections consistent.
- Answer ledger commands from Payments exactly once per `operationId`.
- Publish account and journal events.

Not responsible for: deciding whether a payment should happen, talking to the bank,
risk rules, who may capture or refund (Payments checks that).

## 2. Domain

| Concept | Values |
|---|---|
| Account type | `CustomerWallet`, `MerchantWallet`, `System` |
| Account status | `Active`, `Frozen`, `Closed` |
| System account codes | `BANK_CLEARING`, `PAYMENTS_PENDING` (one of each per currency) |
| Supported currencies | `TRY`, `EUR`, `USD` (configuration `Ledger:Currencies`) |
| Entry type | `TopUp`, `Transfer`, `Hold`, `Capture`, `Release`, `Refund` |
| Line direction | `Debit`, `Credit` |
| Hold status | `Active`, `Captured`, `Released` |

`balance_minor = credits - debits`. Entry line layouts are in `docs/design.md` section 8.

System accounts are created by the migration seed, one pair per configured currency,
with deterministic IDs: UUIDv5 of `"<code>:<currency>"` in namespace
`6f1c2a4e-0d53-4c8b-9a77-1e5b3c9d2f10`.

## 3. HTTP API

Base path at the service: `/`. Through the gateway: `/api/accounts/...` and
`/api/ledger/...`. All endpoints need a valid JWT.

### `POST /accounts` (roles: customer, merchant)

Headers: `Idempotency-Key`.

```json
{ "type": "CustomerWallet", "currency": "TRY" }
```

Rules: `type` must be `CustomerWallet` for role customer and `MerchantWallet` for role
merchant. One account per `(owner, type, currency)`; a second request returns
`409 invalid-state`. `ownerId` is the `sub` claim, `contactEmail` is the `email` claim.

`201 Created`, `Location: /accounts/{id}`:

```json
{
  "id": "0193a7f2-6c1e-7b3a-9d2e-5f8a1c4b7e90",
  "ownerId": "8f14e45f-ceea-467f-a9d1-3a1b2c3d4e5f",
  "type": "CustomerWallet",
  "currency": "TRY",
  "status": "Active",
  "balanceMinor": 0,
  "heldMinor": 0,
  "version": 1,
  "openedAt": "2026-11-13T10:15:30Z"
}
```

`heldMinor` is the sum of this account's `Active` holds.

### `GET /accounts` (owner)

Lists the caller's accounts. Same item shape. Not paginated (at most 6 per owner).

### `GET /accounts/{id}` (owner or admin)

Same shape. `404 not-found` if it does not exist or is not the caller's (no existence
leak).

### `GET /accounts/{id}/entries?limit=&cursor=` (owner or admin)

Newest first. The cursor encodes `global_seq`.

```json
{
  "items": [
    {
      "journalEntryId": "0193a7f3-0000-7000-8000-00000000000a",
      "entryType": "Transfer",
      "direction": "Debit",
      "amountMinor": 2500,
      "currency": "TRY",
      "balanceAfterMinor": 7500,
      "paymentId": "0193a7f2-5a00-7000-8000-000000000001",
      "postedAt": "2026-11-13T10:16:02Z"
    }
  ],
  "nextCursor": null
}
```

### `POST /accounts/{id}/freeze`, `POST /accounts/{id}/unfreeze` (admin)

No body. `204`. Freezing an already frozen account is a no-op `204`. System accounts
cannot be frozen: `409 invalid-state`.

### `GET /ledger/trial-balance` (admin)

```json
{
  "asOf": "2026-11-13T10:20:00Z",
  "currencies": [
    { "currency": "TRY", "sumOfBalancesMinor": 0, "accounts": 42, "journalEntries": 1310 }
  ]
}
```

`sumOfBalancesMinor` must be 0. The value also feeds the gauge
`ledgerline.ledger.imbalance`.

## 4. Messages

Exchange `ledgerline.accounts`. Queue `accounts.inbox`. C# records live in
`Ledgerline.Contracts.Accounts`. All amounts are minor units.

### Commands consumed

Every command carries `operationId` (UUID, chosen by Payments, stable across retries)
and `paymentId`.

| Type | Payload fields | Entry |
|---|---|---|
| `accounts.command.credit-top-up.v1` | `operationId`, `paymentId`, `accountId`, `amountMinor`, `currency` | `TopUp` |
| `accounts.command.transfer-funds.v1` | `operationId`, `paymentId`, `fromAccountId`, `toAccountId`, `amountMinor`, `currency` | `Transfer` |
| `accounts.command.reserve-funds.v1` | `operationId`, `paymentId`, `accountId`, `amountMinor`, `currency` | `Hold` |
| `accounts.command.capture-funds.v1` | `operationId`, `paymentId`, `merchantAccountId`, `captureAmountMinor` | `Capture` |
| `accounts.command.release-funds.v1` | `operationId`, `paymentId` | `Release` |
| `accounts.command.refund-funds.v1` | `operationId`, `paymentId`, `refundId`, `merchantAccountId`, `customerAccountId`, `amountMinor`, `currency` | `Refund` |

### Replies published

| Type | Payload |
|---|---|
| `accounts.event.ledger-operation-succeeded.v1` | `operationId`, `paymentId`, `kind`, `journalEntryId`, `postedAt` |
| `accounts.event.ledger-operation-rejected.v1` | `operationId`, `paymentId`, `kind`, `reasonCode` |

`kind` is one of `CreditTopUp`, `TransferFunds`, `ReserveFunds`, `CaptureFunds`,
`ReleaseFunds`, `RefundFunds`.

`reasonCode`: `AccountNotFound`, `AccountFrozen`, `AccountClosed`, `CurrencyMismatch`,
`InsufficientFunds`, `SameAccount`, `HoldNotFound`, `HoldNotActive`,
`AmountExceedsHold`, `InvalidAmount`.

Which side must be `Active`: the debited wallet and the credited wallet for every
entry, except that `Release` and the remainder credit of `Capture` always succeed even
if the customer account is frozen (money returns to its owner).

### Events published

`accounts.event.account-opened.v1`

```json
{ "accountId": "...", "ownerId": "...", "type": "CustomerWallet", "currency": "TRY",
  "contactEmail": "alice@example.test", "openedAt": "2026-11-13T10:15:30Z" }
```

`accounts.event.account-status-changed.v1`

```json
{ "accountId": "...", "status": "Frozen", "changedAt": "2026-11-13T11:00:00Z" }
```

`accounts.event.journal-entry-posted.v1`

```json
{
  "journalEntryId": "...", "entryType": "Capture", "paymentId": "...", "currency": "TRY",
  "postedAt": "2026-11-13T10:16:02Z",
  "lines": [
    { "accountId": "...", "direction": "Debit",  "amountMinor": 10000, "balanceAfterMinor": 0 },
    { "accountId": "...", "direction": "Credit", "amountMinor": 8000,  "balanceAfterMinor": 8000 },
    { "accountId": "...", "direction": "Credit", "amountMinor": 2000,  "balanceAfterMinor": 2000 }
  ]
}
```

`contactEmail` is personal data. It appears only in `account-opened` and is consumed
only by Notifications. It must not be logged.

## 5. Database schema (`accounts_db`)

```sql
CREATE TABLE events (
    global_seq  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    stream_id   uuid        NOT NULL,          -- account id
    version     int         NOT NULL,
    type        text        NOT NULL,          -- 'account-opened.v1', 'ledger-entry-posted.v1', 'account-status-changed.v1'
    data        jsonb       NOT NULL,
    metadata    jsonb       NOT NULL,          -- correlationId, causationId, operationId
    occurred_at timestamptz NOT NULL,
    UNIQUE (stream_id, version)
);

CREATE TABLE accounts (                         -- projection
    id            uuid PRIMARY KEY,
    owner_id      uuid        NULL,             -- null for system accounts
    type          text        NOT NULL,
    system_code   text        NULL,
    currency      char(3)     NOT NULL,
    status        text        NOT NULL,
    balance_minor bigint      NOT NULL DEFAULT 0,
    version       int         NOT NULL,
    contact_email text        NULL,
    idempotency_key text      NULL,
    request_hash  text        NULL,
    opened_at     timestamptz NOT NULL,
    CONSTRAINT ck_wallet_non_negative CHECK (type = 'System' OR balance_minor >= 0),
    CONSTRAINT uq_owner_type_currency UNIQUE (owner_id, type, currency),
    CONSTRAINT uq_system UNIQUE (system_code, currency),
    CONSTRAINT uq_account_idem UNIQUE (owner_id, idempotency_key)
);

CREATE TABLE journal_entries (                  -- projection
    id           uuid PRIMARY KEY,
    entry_type   text        NOT NULL,
    operation_id uuid        NOT NULL UNIQUE,
    payment_id   uuid        NOT NULL,
    currency     char(3)     NOT NULL,
    posted_at    timestamptz NOT NULL
);
CREATE INDEX ix_journal_payment ON journal_entries (payment_id);

CREATE TABLE journal_lines (                    -- projection
    journal_entry_id    uuid   NOT NULL REFERENCES journal_entries (id),
    line_no             int    NOT NULL,
    account_id          uuid   NOT NULL REFERENCES accounts (id),
    direction           text   NOT NULL,
    amount_minor        bigint NOT NULL CHECK (amount_minor > 0),
    balance_after_minor bigint NOT NULL,
    event_global_seq    bigint NOT NULL,
    PRIMARY KEY (journal_entry_id, line_no)
);
CREATE INDEX ix_lines_account ON journal_lines (account_id, event_global_seq DESC);

CREATE TABLE holds (                            -- projection
    payment_id   uuid PRIMARY KEY,
    account_id   uuid   NOT NULL REFERENCES accounts (id),
    amount_minor bigint NOT NULL CHECK (amount_minor > 0),
    currency     char(3) NOT NULL,
    status       text   NOT NULL
);

CREATE TABLE ledger_operations (                -- business idempotency
    operation_id     uuid PRIMARY KEY,
    kind             text        NOT NULL,
    payment_id       uuid        NOT NULL,
    outcome          text        NOT NULL,      -- 'Succeeded' | 'Rejected'
    reason_code      text        NULL,
    journal_entry_id uuid        NULL,
    completed_at     timestamptz NOT NULL
);

-- plus outbox_messages and inbox_messages from building-blocks.md
```

Event payloads:

- `account-opened.v1`: `ownerId`, `type`, `systemCode`, `currency`, `contactEmail`.
- `ledger-entry-posted.v1`: `journalEntryId`, `entryType`, `paymentId`, `direction`,
  `amountMinor`, `currency`, `balanceAfterMinor`.
- `account-status-changed.v1`: `status`.

## 6. Posting algorithm

One function, used by every command handler:

```csharp
// Ledgerline.Accounts.Ledger
public sealed record PostingLine(Guid AccountId, Direction Direction, long AmountMinor);
public sealed record PostingRequest(Guid OperationId, Guid PaymentId, EntryType EntryType,
    string Currency, IReadOnlyList<PostingLine> Lines, IReadOnlySet<Guid> SkipStatusCheckFor);
public abstract record PostingResult
{
    public sealed record Posted(Guid JournalEntryId, DateTimeOffset PostedAt) : PostingResult;
    public sealed record Rejected(string ReasonCode) : PostingResult;
}
public interface ILedger
{
    Task<PostingResult> PostAsync(PostingRequest request, CancellationToken ct);
}
```

Steps inside the consumer pipeline's transaction:

1. Look up `ledger_operations` by `operationId`. Found: re-publish the stored reply
   and return (same outcome as the first time).
2. Validate the request shape: at least two lines, all amounts positive, total debits
   equal total credits, no account appears twice. Violations are programming errors
   and throw (the message will dead-letter); they are not business rejections.
3. `SELECT * FROM accounts WHERE id = ANY(@ids) ORDER BY id FOR UPDATE`.
4. Business checks in order: all accounts exist; currencies match the request;
   statuses are `Active` (except IDs in `SkipStatusCheckFor`); for each debited wallet,
   `balance_minor - debit >= 0`. First failure gives the rejection reason.
5. Rejected: insert `ledger_operations (Rejected)`, enqueue the rejected reply. Commit.
6. Posted: for each line append a `ledger-entry-posted.v1` event with
   `version = accounts.version + 1`; update `accounts.balance_minor` and `version`;
   insert `journal_entries` and `journal_lines`; update `holds` if the entry type
   affects one; insert `ledger_operations (Succeeded)`; enqueue the succeeded reply
   and `journal-entry-posted.v1`. Commit.

Hold-specific rules:

- `ReserveFunds`: a hold for the `paymentId` already exists: `HoldNotActive`.
- `CaptureFunds`: hold must exist (`HoldNotFound`) and be `Active` (`HoldNotActive`);
  `captureAmountMinor <= hold.amount_minor` (`AmountExceedsHold`). The entry debits
  `PAYMENTS_PENDING` by the full hold, credits the merchant by the captured amount, and
  credits the customer by the remainder when it is greater than zero.
- `ReleaseFunds`: hold must exist and be `Active`. An already `Released` hold gives
  `HoldNotActive`.

## 7. Invariants

| # | Invariant | Enforced by |
|---|---|---|
| A1 | Sum of `balance_minor` per currency is zero | Balanced-entry validation; trial balance check after every test; gauge |
| A2 | Every journal entry has debits equal to credits | Step 2 of posting |
| A3 | A wallet balance is never negative | Step 4, plus `ck_wallet_non_negative` |
| A4 | Events are append-only; `(stream_id, version)` has no gaps or duplicates | Unique constraint; no UPDATE or DELETE grants for the service login on `events` |
| A5 | Projections equal a replay of `events` | Rebuild test |
| A6 | One `operationId` produces at most one journal entry and always the same reply | `ledger_operations` primary key, `journal_entries.operation_id` unique |
| A7 | A hold is captured or released at most once, and capture never exceeds it | Hold status transition under row lock |
| A8 | Balances change only through `ILedger.PostAsync` | Architecture test: only the ledger namespace writes `accounts.balance_minor` |

## 8. Idempotency

- HTTP: `Idempotency-Key` on `POST /accounts` per `building-blocks.md`.
- Messages: inbox by `messageId`, then `ledger_operations` by `operationId`.
- A command redelivered after commit but before acknowledge: stopped by the inbox.
- The same command re-sent by the saga after a timeout (new `messageId`, same
  `operationId`): the stored reply is published again. No second entry.

## 9. Failure handling

| Failure | Behavior |
|---|---|
| Broker down | HTTP API keeps working. Replies and events accumulate in the outbox and flow when the broker returns |
| Crash during posting | The transaction rolls back. The command is redelivered and processed once |
| Crash after commit, before acknowledge | Redelivery is absorbed by the inbox |
| Deadlock or serialization failure | Not expected because of ordered locking. If PostgreSQL reports `40P01` or `40001`, the pipeline retries the handler up to 3 times before the normal retry queue path |
| Malformed command (unbalanced, unknown account type) | Throws, retries, then dead-letters. Payments' step timeout will move the payment to `ManualReview` |
| Constraint violation `ck_wallet_non_negative` | Treated as a bug: transaction fails, message dead-letters after retries, alert on `dead_lettered` |

## 10. Admin tooling

`dotnet Ledgerline.Accounts.dll migrate` applies migrations and seeds system accounts.
`dotnet Ledgerline.Accounts.dll rebuild-projections` truncates the four projection
tables and replays `events` in `global_seq` order inside one transaction.
