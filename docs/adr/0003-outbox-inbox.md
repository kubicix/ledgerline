# ADR 0003: Transactional outbox with a polling relay, inbox table for deduplication

Date: 2026-11-13. Status: accepted.

## Context

A service must change its own state and publish a message as one atomic step. The
database and the broker cannot share a transaction. Consumers will see duplicates
(redelivery after a lost acknowledgment, relay retry after a lost confirm).

## Decision

**Outbox.** Each service database has an `outbox_messages` table. Application code
adds outbox rows through the same `DbContext` and `SaveChanges` as its state change.
A hosted relay per service:

1. Selects a batch of unprocessed rows ordered by `sequence` with
   `FOR UPDATE SKIP LOCKED` inside a transaction.
2. Publishes each with publisher confirms and the `mandatory` flag.
3. Sets `processed_at` for confirmed rows and commits.

The relay polls every 250 ms and is also woken by an in-process signal after a commit
that added rows. Failed publishes increase `attempts`, record `last_error`, and are
retried with capped exponential backoff. Rows are never dropped by the relay.

**Inbox.** Each service database has an `inbox_messages` table with primary key
`(message_id, consumer)`. The consumer pipeline opens a transaction, inserts the inbox
row with `ON CONFLICT DO NOTHING`, and:

- zero rows inserted: the message is a duplicate. Acknowledge, count a metric, stop.
- one row inserted: run the handler in the same transaction (state changes plus any
  outbox rows), commit, then acknowledge.

**Retention.** A cleanup job deletes processed outbox rows and inbox rows older than
7 days.

## Why polling and not change data capture

Debezium or logical replication would remove polling latency and load. They also add a
connector to run and monitor, and they hide the mechanism this project wants to show.
Polling with `SKIP LOCKED` is simple, safe with several relay replicas, and its cost
is measurable (outbox lag metric, k6).

## Guarantees and limits

- Delivery is at least once. Duplicates are expected and absorbed by the inbox.
- A crash between publish and marking processed causes a re-publish: a duplicate.
- A crash between handler commit and acknowledge causes a redelivery: a duplicate.
- Ordering: the relay publishes in `sequence` order per service, but with several
  relay replicas or retries, order across messages is not guaranteed. Handlers must
  not rely on order. The saga guards transitions by current state instead.
- The inbox protects against the same `messageId` only. A logically repeated command
  with a new `messageId` (saga retry) is handled by business idempotency keys
  (`operationId` in Accounts, `paymentId` in Risk).
- Inbox retention shorter than the longest possible redelivery delay would break
  deduplication. Seven days against a retry horizon of minutes leaves a wide margin.

## Consequences

- Every handler that changes state must go through the inbox pipeline. An
  architecture test checks that consumers are registered only through it.
- Outbox lag is a first-class metric and a k6 threshold.
- Tests required by this ADR are listed in `docs/testing.md` (broker down, crash
  after publish, duplicate delivery).
