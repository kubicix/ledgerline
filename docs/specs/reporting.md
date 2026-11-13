# Spec: Reporting (Phase 2, CQRS read side)

Project `src/Services/Reporting/Ledgerline.Reporting` (created in M5). MongoDB
database `ledgerline_reporting`. Consumes Kafka. Publishes nothing.

Versions to re-verify at M5: `apache/kafka` 4.4.0, `mongo` 9.0, `Confluent.Kafka`
2.16.0, `MongoDB.Driver` 3.14.0 (ADR 0000).

## 1. Responsibilities

- Maintain query-shaped read models from the event stream.
- Serve read-only HTTP queries that the write services should not have to serve.
- Be disposable: dropping the database and replaying the topics restores it.

Not a source of truth. If a read model and the ledger disagree, the ledger is right.

## 2. Kafka topics (produced by Accounts and Payments)

| Topic | Key | Value | Producer |
|---|---|---|---|
| `ledgerline.ledger.entries.v1` | `accountId` of the line | Envelope with `accounts.event.journal-entry-posted.v1`, one record per line (`lineNo` added to the payload) | Accounts outbox relay, `transport = 'kafka'` |
| `ledgerline.payments.events.v1` | `paymentId` | Envelope with any `payments.event.*` type | Payments outbox relay, `transport = 'kafka'` |

Settings: 6 partitions, replication factor 1 (single dev broker), retention unlimited
(`retention.ms = -1`), `cleanup.policy = delete`. Producer: idempotent, `acks = all`.

Keying by account and by payment gives per-account and per-payment ordering, which the
projections rely on.

How the outbox feeds Kafka: when a service enqueues an event that is configured for
both transports, two outbox rows are written in the same transaction (one per
transport, different `message_id`, same payload). The relay has one publisher per
transport. A Kafka outage therefore never delays RabbitMQ and the other way round.

## 3. Consumer

Group `reporting`. Manual offset commit after the batch's writes are acknowledged by
MongoDB. Processing is at least once; every write is idempotent (below), so replays
and duplicates are harmless.

## 4. Read models (MongoDB collections)

### `account_statements`

One document per ledger line. `_id = "<journalEntryId>:<lineNo>"`.

```json
{ "_id": "0193...:1", "accountId": "...", "journalEntryId": "...", "entryType": "Transfer",
  "direction": "Debit", "amountMinor": 2500, "currency": "TRY", "balanceAfterMinor": 7500,
  "paymentId": "...", "postedAt": { "$date": "2026-11-13T10:16:02Z" } }
```

Index: `{ accountId: 1, postedAt: -1 }`. Write: upsert by `_id` (replace).

### `payment_timelines`

One document per payment. `_id = paymentId`.

```json
{ "_id": "...", "kind": "MerchantPayment", "status": "Captured", "amountMinor": 10000,
  "currency": "TRY", "customerId": "...", "sourceAccountId": "...", "destinationAccountId": "...",
  "capturedMinor": 8000, "refundedMinor": 0,
  "events": [ { "messageId": "...", "type": "payment-authorized", "at": { "$date": "..." } } ],
  "updatedAt": { "$date": "..." } }
```

Write: `$addToSet` on `events` by `messageId` semantics (update with a filter
`events.messageId != messageId`), and `$set` of the summary fields only when the
event's `occurredAt` is not older than `updatedAt`.

### `merchant_daily_totals`

`_id = "<merchantAccountId>:<yyyy-MM-dd>:<currency>"`.

```json
{ "_id": "...", "merchantAccountId": "...", "day": "2026-11-13", "currency": "TRY",
  "capturedMinor": 480000, "refundedMinor": 2500, "captureCount": 37, "refundCount": 1,
  "appliedMessageIds": ["..."] }
```

Counters cannot be upserted idempotently by replacement, so each update is guarded:
`updateOne({ _id, appliedMessageIds: { $ne: messageId } }, { $inc: ..., $push: { appliedMessageIds: messageId } })`.
The array is capped to the last 1 000 IDs with `$slice`; Kafka redelivery windows are
far shorter than that. This trade-off is stated in the README.

## 5. HTTP API

Through the gateway under `/api/reporting/...`.

| Method and path | Access | Result |
|---|---|---|
| `GET /accounts/{accountId}/statement?from=&to=&limit=&cursor=` | owner or admin | Page of statement lines |
| `GET /payments/{paymentId}/timeline` | party or admin | The timeline document |
| `GET /merchants/{merchantAccountId}/daily-totals?from=&to=` | owner or admin | List of daily totals |

Ownership: Reporting keeps `account_owners` (`_id = accountId`, `ownerId`) built from
`account-opened` events, which Accounts also relays to the ledger topic's sibling
`ledgerline.accounts.events.v1` (key `accountId`).

## 6. Invariants

| # | Invariant | Proven by |
|---|---|---|
| Q1 | Applying the same record twice leaves every collection unchanged | Duplicate-apply test per collection |
| Q2 | Dropping the database and replaying from offset zero gives documents equal to the previous ones | Rebuild test |
| Q3 | For every account, the newest `balanceAfterMinor` in `account_statements` equals the ledger balance once the consumer has caught up | Consistency test against Accounts |

## 7. Failure handling

| Failure | Behavior |
|---|---|
| Kafka down | Write services are unaffected; their Kafka outbox rows wait. Reporting serves stale data |
| MongoDB down | Consumer stops committing offsets and retries with backoff. No data loss |
| Poison record | After 5 attempts, written to topic `ledgerline.reporting.dead.v1` with the error, offset committed |
| Consumer lag | Exposed as metric `ledgerline.reporting.lag`. The API adds header `X-Read-Model-Lag-Seconds` |

## 8. Why this is CQRS and what it costs

Writes go to PostgreSQL in the owning services. Reads that span time ranges or
aggregates go to MongoDB documents shaped for the query. The cost is eventual
consistency (a statement can be seconds behind) and a second datastore to run. The
README states that for this data volume a PostgreSQL view would do the same job, and
that the read model exists to demonstrate the pattern.
