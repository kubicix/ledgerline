# ADR 0007: Event sourcing for the ledger only, hand-rolled on PostgreSQL

Date: 2026-11-13. Status: accepted (owner decision on hand-rolled vs Marten).

## Context

Event sourcing gives an append-only audit trail and the ability to rebuild state. It
costs complexity: versioning of events, projections, and unfamiliar debugging. Marten
(9.59.0, MIT) offers a mature event store on PostgreSQL.

## Decision

**Scope.** Only Accounts and Ledger is event-sourced. A ledger is append-only by
nature; corrections are new entries, never updates. Payments, Risk, and Notifications
store current state in ordinary tables. Payments keeps an audit log of saga steps,
which is a log, not a source of truth.

**Implementation.** Hand-rolled, in `accounts_db`:

- `events (global_seq bigserial, stream_id, version, type, data jsonb, metadata jsonb,
  occurred_at)`, unique `(stream_id, version)`. One stream per account.
- Projections `accounts`, `journal_entries`, `journal_lines`, `holds` are updated in
  the same transaction as the event append (inline projections). Reads never fold
  events on the request path.
- A journal entry touches two or three account streams. All its events are appended
  in one PostgreSQL transaction, so the entry is atomic.

**Concurrency.** A posting takes `SELECT ... FOR UPDATE` on the affected `accounts`
rows in ascending ID order, validates (status, currency, balance), appends events with
`version = current + 1` per stream, updates projections. Sorted locking prevents
deadlocks. The unique constraint and a `CHECK (type = 'System' OR balance_minor >= 0)`
are safety nets that turn a logic bug into a failed transaction, not lost money.

Optimistic concurrency with retries was rejected for postings: system accounts take
part in most entries, and every concurrent posting would conflict and retry.

**Why not Marten.** The mechanics are the thing being demonstrated, and the feature
set needed is small (append, per-stream version, inline projection, rebuild). Marten
would add a second persistence style next to EF Core. For a product with async
projections and many aggregates, Marten would be the recommendation.

## Consequences

- Rows on `BANK_CLEARING` and `PAYMENTS_PENDING` are locked by most postings of a
  currency, so postings serialize per currency. This caps throughput. It is measured
  by a dedicated k6 scenario. The documented mitigation is to shard system accounts
  (N sub-accounts chosen by hash of the payment ID). It is built only if measurements
  justify it.
- Event schema changes need upcasting. Rule: events are immutable; a changed shape is
  a new event type version, and the fold handles both.
- A rebuild test is mandatory: truncate projections, replay `events`, compare with the
  previous projection state byte for byte.
- No snapshots. Streams are never folded on the request path, so stream length does
  not affect latency.
- Nothing here is derived from the owner's employer code. The design follows public
  descriptions of the pattern.

## Measured: hot account (M3, 2026-11-26)

The k6 scenario `transfer-hot-account` sends 100 transfers per second for 3 minutes, every one into the same
merchant wallet, so every posting locks that one `accounts` row. A transfer posts customer to merchant only
(`Entries.Transfer`); it touches no system account. It ran on one desktop PC (Ryzen 5 7500F, 32 GB, DRAM-less NVMe
under Docker Desktop), with the load generator on the same machine. Full results: [k6 results](../results/k6-2026-11-26.md).

| Metric | Target | transfer-hot-account | transfer-steady (random pairs, same offered rate) |
|---|---|---|---|
| Accept latency p95 / p99 | 150 / 400 ms | 467.57 / 895.86 ms | 252.12 / 1 074.83 ms |
| Saga completion p95 (k6, 30 s timeout) | 1 000 ms | 30.3 s (timeout) | 30.2 s (timeout) |
| Saga completion p95 (server side) | | 218 s | 174.9 s |
| Transfers sent by k6 (ceiling: 400 VUs over the 30 s poll timeout) | 100/s | 13.5/s | 13.5/s |
| Postings completed during the load | | about 1.75/s (105 per minute) | 6.2/s |
| Postings after the load stopped | | 1 612 drained in about 50 s (about 32/s) | 2 198 in about 110 s (about 20/s) |
| Failed requests, ManualReview, trial balance | 0 | 0, 0, 0 | 0, 0, 0 |

Both scenarios received the same load, because k6 could start only as many transfers as its 400 VUs finished
polling, and both missed by the same order of magnitude. The limits on this machine were the disk, which stalls for
about 17 s every 55 to 90 s under the fsync load of commits and quorum-queue confirms, and the outbox relay, which
publishes and confirms one message at a time (Payments outbox lag p95 of 61 to 96 s, Accounts and Risk below 6 s).
The hot row was not the binding limit: after the load stopped, the backlog drained into that one row at about 32
postings per second, faster than the whole pipeline completed transfers during either run. The lower completion rate
during the hot-account load is a single run on a disk whose stall windows vary run to run, so it does not isolate
row-lock contention either. One more confounder: the OpenTelemetry collector in the telemetry container ran out of
memory under load in the transfer-steady run (and, in a retry with a 6 GiB limit, used 6 to 7 cores' worth of CPU
near its limit). Whether the same happened in the hot-account run is unknown, so the two runs may have had different
CPU competition.

**Decision: accept, do not shard.** The hot merchant row stays a single row, and the system accounts stay unsharded.
Sharding would also not apply to this scenario as written: the documented mitigation splits `BANK_CLEARING` and
`PAYMENTS_PENDING`, and a transfer touches neither; splitting a merchant's own wallet would change the account model
that payments and refunds rely on.

**What would reopen it.** A hot-account run, on a host without the disk stall and with the relay no longer the limit
(for example batched publisher confirms), where postings into the hot row complete at less than half the rate of
`transfer-steady` with random pairs, or where Postgres shows sustained lock waits on that row. A similar run of
`payment-mix` that reaches capture under load would answer the same question for `PAYMENTS_PENDING`, which this run
did not exercise.
