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
