# Spec: Building blocks (messaging, HTTP conventions, shared types)

Shared by all services. Decisions: ADR 0002, 0003, 0009. Projects:

| Project | Contents |
|---|---|
| `Ledgerline.BuildingBlocks` | `Money`, `Result`, `IClock`, UUIDv7 IDs. No I/O, no ASP.NET |
| `Ledgerline.BuildingBlocks.Messaging` | Envelope, outbox, inbox, RabbitMQ publisher, consumer, relay, topology |
| `Ledgerline.BuildingBlocks.Web` | Idempotency filter, problem details, JWT setup, health checks, OpenTelemetry setup |
| `Ledgerline.Contracts` | Message records only. No logic. References `BuildingBlocks.Messaging` solely for `MessageTypeAttribute` |

## 1. Shared types

```csharp
namespace Ledgerline.BuildingBlocks;

public readonly record struct Money(long AmountMinor, string Currency);   // Currency: ISO 4217, upper case
public interface IClock { DateTimeOffset UtcNow { get; } }
public static class Ids { public static Guid New(); }                     // Guid.CreateVersion7()
```

`Money` rules: `AmountMinor > 0` for any requested amount; adding or comparing two
values with different currencies throws `CurrencyMismatchException`. Maximum
`AmountMinor` accepted at any API: `1_000_000_000_00` (one billion major units).

## 2. Message envelope

JSON body, UTF-8, camelCase:

```json
{
  "messageId": "0193a7f2-6c1e-7b3a-9d2e-5f8a1c4b7e90",
  "type": "accounts.command.reserve-funds.v1",
  "occurredAt": "2026-11-13T10:15:30.123Z",
  "correlationId": "0193a7f2-5a00-7000-8000-000000000001",
  "causationId": "0193a7f2-5b11-7000-8000-000000000002",
  "traceParent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "payload": { }
}
```

| Field | Rule |
|---|---|
| `messageId` | UUIDv7, unique per message instance. The inbox key |
| `type` | `<owner>.<command|event>.<kebab-name>.v<N>` |
| `correlationId` | The payment ID for everything in a payment flow; otherwise the request's ID |
| `causationId` | `messageId` of the message whose handler produced this one; null for HTTP-originated |
| `traceParent` | W3C trace context captured when the outbox row was created |

AMQP properties mirror the envelope: `message_id`, `type`, `correlation_id`,
`content_type = application/json`, `delivery_mode = 2`, header `traceparent`,
header `x-ledgerline-attempt` (integer, starts at 1).

## 3. Versioning

- The version is part of the type name and of the routing key.
- Within a version only additive, optional fields are allowed. Consumers ignore
  unknown fields and must tolerate missing optional fields.
- A breaking change is a new type (`...v2`). The publisher emits both versions until
  every consumer has moved, then stops emitting v1. Contract tests pin a JSON sample
  per type in `tests/Ledgerline.BuildingBlocks.Tests/Contracts/Samples/`.

## 4. Topology

| Object | Name | Properties |
|---|---|---|
| Exchange per owner | `ledgerline.<owner>` (`accounts`, `payments`, `risk`, `reconciliation`) | topic, durable |
| Routing key | the message `type` | |
| Queue per consuming service | `<service>.inbox` | quorum, durable |
| Retry queue | `<service>.inbox.retry` | quorum, `x-message-ttl = 5000`, dead-letters to the default exchange with routing key `<service>.inbox` |
| Dead queue | `<service>.inbox.dead` | quorum, no consumer |

The exchange for a message is derived from the first segment of its type. A service
declares its own exchange, its three queues, and one binding per consumed type at
startup (idempotent declarations). Commands are published to the exchange of the
service that owns the command contract (the receiver).

## 5. Public API of the messaging layer

```csharp
namespace Ledgerline.BuildingBlocks.Messaging;

[AttributeUsage(AttributeTargets.Class)]
public sealed class MessageTypeAttribute(string name) : Attribute { public string Name { get; } }

public sealed record MessageContext(Guid MessageId, Guid CorrelationId, Guid? CausationId, int Attempt);

public interface IMessageHandler<in TMessage>
{
    Task HandleAsync(TMessage message, MessageContext context, CancellationToken ct);
}

public interface IOutbox
{
    // Adds an outbox row to the scoped DbContext. Nothing is sent until SaveChanges commits.
    void Enqueue<TMessage>(TMessage message, Guid correlationId, Guid? causationId = null);
}

public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddLedgerlineMessaging<TDbContext>(
        this IServiceCollection services, IConfiguration configuration,
        Action<MessagingBuilder> configure) where TDbContext : DbContext;
}

public sealed class MessagingBuilder
{
    public MessagingBuilder Service(string name);                                  // "payments"
    public MessagingBuilder Consume<TMessage, THandler>() where THandler : class, IMessageHandler<TMessage>;
}

public static class ModelBuilderExtensions
{
    public static ModelBuilder AddOutboxAndInbox(this ModelBuilder modelBuilder);  // maps both tables
}
```

Configuration section `Messaging`: `RabbitMq:Uri`, `Relay:PollIntervalMs` (250),
`Relay:BatchSize` (100), `Consumer:Prefetch` (16), `Consumer:MaxAttempts` (5),
`Retention:Days` (7).

## 6. Tables (in every service database that uses messaging)

```sql
CREATE TABLE outbox_messages (
    sequence        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    message_id      uuid        NOT NULL UNIQUE,
    type            text        NOT NULL,
    payload         jsonb       NOT NULL,
    correlation_id  uuid        NOT NULL,
    causation_id    uuid        NULL,
    trace_parent    text        NULL,
    transport       text        NOT NULL DEFAULT 'rabbitmq',   -- 'kafka' added in Phase 2
    occurred_at     timestamptz NOT NULL,
    processed_at    timestamptz NULL,
    attempts        int         NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    last_error      text        NULL
);
CREATE INDEX ix_outbox_pending ON outbox_messages (sequence) WHERE processed_at IS NULL;

CREATE TABLE inbox_messages (
    message_id   uuid        NOT NULL,
    consumer     text        NOT NULL,            -- service name
    type         text        NOT NULL,
    received_at  timestamptz NOT NULL,
    PRIMARY KEY (message_id, consumer)
);
```

## 7. Relay behavior

Loop (per service instance), every `PollIntervalMs` or when signaled:

1. `BEGIN`. `SELECT ... FROM outbox_messages WHERE processed_at IS NULL AND
   next_attempt_at <= now() ORDER BY sequence LIMIT @batch FOR UPDATE SKIP LOCKED`.
2. For each row: publish with `mandatory = true` and wait for the publisher confirm
   (timeout 5 s).
3. Confirmed: set `processed_at = now()`.
   Failed, returned, or timed out: `attempts += 1`, `last_error`,
   `next_attempt_at = now() + min(2^attempts * 100 ms, 30 s)`. Stop the batch at the
   first failure so that order is preserved while the broker is unhealthy.
4. `COMMIT`.

When the broker connection is down the relay does not crash. It logs once per state
change, keeps polling, and resumes when the connection returns (automatic recovery is
enabled; the relay also recreates its channel on failure).

## 8. Consumer pipeline

For each delivery from `<service>.inbox`:

1. Parse the envelope. Unparseable body or unknown `type`: publish to
   `<service>.inbox.dead` with header `x-ledgerline-dead-reason`, acknowledge, count
   `ledgerline.consumer.dead_lettered`.
2. Start a `CONSUMER` span from `traceParent`. Create a DI scope.
3. `BEGIN`. `INSERT INTO inbox_messages ... ON CONFLICT DO NOTHING`.
   Zero rows: `ROLLBACK`, acknowledge, count `ledgerline.inbox.duplicates`. Done.
4. Invoke the handler. The handler uses the same scoped `DbContext` and `IOutbox`.
5. `SaveChanges`, `COMMIT`, then acknowledge.
6. On exception: `ROLLBACK`. If `attempt < MaxAttempts`, publish a copy to
   `<service>.inbox.retry` with `x-ledgerline-attempt + 1` (confirmed), then
   acknowledge the original. Otherwise publish to `.dead` and acknowledge.
   If the retry or dead publish itself fails: negative-acknowledge with requeue.

Handlers must be idempotent beyond the inbox where a business key exists, and must
not perform non-transactional side effects (HTTP calls, email) except where a spec
explicitly describes how duplicates are tolerated.

Graceful shutdown: stop consuming (`BasicCancel`), wait for in-flight handlers up to
20 s, let the relay finish its current batch, close the connection.

## 9. HTTP conventions (`Ledgerline.BuildingBlocks.Web`)

**Errors:** RFC 9457 problem details, `application/problem+json`, with
`type = "urn:ledgerline:problem:<code>"`, `title`, `status`, `detail`, `traceId`.

| Code | Status |
|---|---|
| `validation-failed` (with `errors` map) | 400 |
| `idempotency-key-missing` | 400 |
| `unauthenticated` | 401 |
| `forbidden` | 403 |
| `not-found` | 404 |
| `invalid-state` | 409 |
| `request-in-progress` | 409, `Retry-After: 1` |
| `idempotency-key-reused` | 422 |
| `rate-limited` | 429 |

**Idempotency-Key.** Required on every POST that creates or changes something. 1 to
64 characters from `[A-Za-z0-9_-]`. Scope: the caller (`sub` claim) plus the key.

1. Compute `request_hash = SHA-256(method + path + canonical JSON body)`.
2. Redis fast path, key `idem:<service>:<sub>:<key>`:
   - `SET NX` with value `in-progress:<request_hash>:<leaseId>` and a 30-second
     lease succeeds: continue. A crashed owner cannot block retries for a day.
   - Existing value `in-progress:<same hash>:<leaseId>`: return `409 request-in-progress`.
   - Existing value `done:<hash>:<resourceId>` with the same hash: load the resource
     and return it with the original success status.
   - Any existing value with a different hash: `422 idempotency-key-reused`.
   - Redis unavailable: skip to step 3. Log at warning level, count a metric.
3. The database is the guarantee: the resource row stores `idempotency_key` and
   `request_hash` under a unique `(owner_id, idempotency_key)` constraint. A unique
   violation means "already created": load, compare hash, respond as in step 2.
4. After commit, compare the unique lease identity and set the Redis value to
   `done:<hash>:<resourceId>` with a 24-hour TTL. Completion and release may affect
   only the caller's own lease. After lease expiry, database uniqueness, replay and
   transaction locks remain authoritative, including when an older request is still running.

A replayed request returns the current representation of the resource, not a stored
copy of the first response body. The status code is the original one (`201` or `202`).

**Pagination:** `?limit=` (1 to 100, default 20) and `?cursor=` (opaque). Response:
`{ "items": [...], "nextCursor": "..." | null }`.

**Auth:** JWT bearer, issuer and audience from configuration, `sub` as the owner ID,
realm roles mapped to ASP.NET Core roles. Policies: `Customer`, `Merchant`, `Admin`.

**Health:** `GET /health/live` returns 200 while the process runs.
`GET /health/ready` checks the service's own database and its RabbitMQ connection.
Both are anonymous and are not routed by the gateway.

**Telemetry:** `AddLedgerlineTelemetry(serviceName)` wires traces (ASP.NET Core,
HttpClient, Npgsql, the `Ledgerline.Messaging` source), metrics (runtime, ASP.NET
Core, meter `Ledgerline`), and logs to OTLP at `OTEL_EXPORTER_OTLP_ENDPOINT`.

## 10. Invariants of this layer

1. An outbox row exists if and only if the transaction that produced it committed.
2. An outbox row is never marked processed before the broker confirmed it.
3. A handler's effects are committed at most once per `(messageId, consumer)`.
4. A message is acknowledged only after its transaction committed, or after it was
   safely handed to the retry or dead queue.
5. No message is dropped: every delivery ends as processed, duplicate, retry, or dead.

Each invariant has a named test in `docs/testing.md`.
