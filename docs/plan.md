# Ledgerline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Tasks use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build Ledgerline, a payment and wallet backend as .NET 10 microservices, to the definition of done at the end of this document.

**Architecture:** Four business services (Accounts and Ledger, Payments, Risk, Notifications) plus a YARP gateway and a fake bank, each with its own PostgreSQL database, talking only through RabbitMQ with a hand-rolled outbox and inbox. Payments orchestrates sagas; the ledger is event-sourced and double-entry. Phase 2 adds Kafka, a MongoDB read model, and a reconciliation job.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 with Npgsql, RabbitMQ.Client 7, StackExchange.Redis, YARP, Keycloak, OpenTelemetry, xunit.v3 on Microsoft.Testing.Platform, Shouldly, Testcontainers, Docker Compose, Helm and kind, k6, GitHub Actions.

**Spec:** `docs/design.md`, `docs/specs/*.md`, `docs/adr/*.md`, `docs/testing.md`. The specs hold the schemas, message shapes, state tables, and invariants. This plan says in which order to build them and how each step is proven. Read the spec sections a task names before starting it.

## Global Constraints

- Target framework `net10.0`. Do not move to .NET 11. Microsoft packages stay on `10.0.x`.
- Package versions come from `docs/adr/0000-verified-versions.md`. Every new package gets a `PackageVersion` in `Directory.Packages.props`. If a version there no longer restores, check nuget.org, update the ADR table, then proceed.
- `TreatWarningsAsErrors` is on. A task is not done while `dotnet build Ledgerline.slnx` prints a warning.
- No messaging framework (MassTransit, Wolverine, NServiceBus, MediatR, CAP). No Marten. No FluentAssertions. No Bitnami charts or images.
- A deployable project never references another deployable project. Services use only `BuildingBlocks*` and `Contracts`. Gateway and BankSimulator reference neither `Contracts` nor `BuildingBlocks.Messaging`.
- Services call each other only through messages. The only outbound HTTP calls are Payments and Reconciliation to BankSimulator.
- Money is `long` minor units plus an ISO 4217 code. No `decimal`, `double`, or `float` for amounts anywhere.
- Every state-changing message handler runs through the inbox pipeline. Every published message goes through the outbox.
- Tests wait with `Eventually.ShouldAsync`, never with a fixed delay.
- Every test that moves money ends with `await LedgerAssert.InvariantsHoldAsync(...)`.
- Documentation and code comments are English. No em dashes in any document.
- The project is never described as banking experience. No code, names, or data from any employer project.
- Email addresses are never logged.
- Commit after every task with the message given. Do not push; the owner pushes.

## Review Focus

Inputs and conditions the specs imply that are easy to miss. Each one is pinned by a named test in the task shown.

1. **Amount edge values:** zero, negative, and above `1_000_000_000_00` at every POST must give `400 validation-failed`, and an overflowing sum of two balances must throw, not wrap. Pinned in M1-T01 (`Money_rejects_overflow`) and M1-T21 (`Post_with_invalid_amount_returns_400`).
2. **Same `Idempotency-Key` with a different body** must give `422`, not the first resource. Pinned in M1-T18 and M2-T02.
3. **Account opened a moment ago, not yet replicated to Payments:** POST must give `404` cleanly, not a 500 from a missing `account_refs` row. Pinned in M1-T21 (`Transfer_from_unknown_account_returns_404`).
4. **Reply arriving after the payment is final** (late or duplicated) must be ignored without an exception and without dead-lettering. Pinned in M1-T20 (`Decide_ignores_inputs_in_final_states`).
5. **Broker outage longer than a step timeout** must not push healthy payments to `ManualReview`. Pinned in M2-T17 (`Step_attempts_are_not_consumed_while_broker_is_down`).

## Task Protocol

Every task follows the same cycle. The task text gives the specifics.

1. Read the spec sections listed under **Spec**.
2. Write the tests listed under **Tests**, in the files listed. Use the exact test names.
3. Run the **Prove** command. It must fail (compile error or failed assertion). If it passes before any implementation, the test is wrong: fix the test.
4. Implement what **Implement** describes. Signatures and names given there are binding because later tasks use them.
5. Run the **Prove** command. It must pass.
6. Run `dotnet build Ledgerline.slnx`. Zero warnings, zero errors.
7. Commit with the **Commit** message.

Conventions used below:

- **Depends** lists tasks that must be complete first.
- **Produces** lists names later tasks rely on.
- Integration tests need Docker running.
- Prove commands are run from the repository root. Filters use the Microsoft.Testing.Platform options `--filter-class` and `--filter-method` (verified with xunit.v3 4.0.3).
- Each service project uses folders `Domain/`, `Features/<FeatureName>/` (endpoint or handler plus its types), `Infrastructure/` (DbContext, migrations, clients). Do not create per-layer projects.

## File Structure

```
compose.yaml                               infrastructure; app, phase2, ui profiles added by tasks
Directory.Build.props, Directory.Packages.props, global.json, Ledgerline.slnx
deploy/compose/                            postgres init, keycloak realm, grafana provisioning
deploy/helm/ledgerline/                    M4
deploy/observability/dashboards/           M3
src/BuildingBlocks/Ledgerline.BuildingBlocks/            Money, Ids, IClock, Result
src/BuildingBlocks/Ledgerline.BuildingBlocks.Messaging/  envelope, outbox, inbox, RabbitMQ, relay, consumer
src/BuildingBlocks/Ledgerline.BuildingBlocks.Web/        problem details, auth, idempotency, health, telemetry
src/Contracts/Ledgerline.Contracts/                      message records: Accounts/, Payments/, Risk/, Reconciliation/
src/Services/Accounts/Ledgerline.Accounts/               ledger
src/Services/Payments/Ledgerline.Payments/               sagas
src/Services/Risk/Ledgerline.Risk/
src/Services/Notifications/Ledgerline.Notifications/
src/Services/BankSimulator/Ledgerline.BankSimulator/
src/Services/Reporting/Ledgerline.Reporting/             M5
src/Services/Reconciliation/Ledgerline.Reconciliation/   M5
src/Gateway/Ledgerline.Gateway/
src/Web/Ledgerline.Web/                                  M6
tests/Ledgerline.Testing/                                shared fixtures (M1-T03)
tests/Ledgerline.*.Tests, *.IntegrationTests, ArchitectureTests, SystemTests
tests/k6/                                                M3
```

Existing skeleton (already builds green): all projects above except the ones marked
M1-T03, M3, M4, M5, M6. Each service `Program.cs` is an empty host.

---

# Milestone 1: first version

Accounts and Ledger, Payments (transfer and a simplified top-up), RabbitMQ with outbox
and inbox, one command to run, README. No Risk, no bank, no gateway, no real auth yet:
M1 uses a development header scheme that M2-T01 replaces.

### M1-T01: Money, Ids, clock

- [x] Done

**Spec:** `building-blocks.md` section 1. **Depends:** none.
**Files:** Create `src/BuildingBlocks/Ledgerline.BuildingBlocks/{Money.cs,CurrencyMismatchException.cs,Ids.cs,IClock.cs,SystemClock.cs}`. Test `tests/Ledgerline.BuildingBlocks.Tests/MoneyTests.cs`.
**Produces:** `Money(long AmountMinor, string Currency)` with `Money.Create(long, string)` (validating factory, throws `ArgumentException`), operators `+`, `-`, `Money.MaxAmountMinor = 1_000_000_000_00`; `Ids.New()`; `IClock.UtcNow`; `SystemClock`.
**Tests:** `Create_rejects_zero_and_negative_amounts`; `Create_rejects_amount_above_maximum`; `Create_rejects_currency_not_three_upper_case_letters` (cases: `"try"`, `"TRYX"`, `""`); `Add_with_different_currencies_throws_CurrencyMismatchException`; `Money_rejects_overflow` (`long.MaxValue` plus 1 throws `OverflowException`); `Ids_New_returns_version_7_guid` (`Version == 7`).
**Implement:** arithmetic in `checked` context. `Money` default constructor stays usable for EF but `Create` is the only validated entry.
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Tests --filter-class "*MoneyTests"`
**Commit:** `feat(blocks): add Money, Ids and clock`

### M1-T02: Message envelope, type names, serializer

- [x] Done

**Spec:** `building-blocks.md` sections 2, 3, 5. **Depends:** M1-T01.
**Files:** Create in `src/BuildingBlocks/Ledgerline.BuildingBlocks.Messaging/`: `MessageEnvelope.cs`, `MessageTypeAttribute.cs`, `MessageTypeRegistry.cs`, `EnvelopeSerializer.cs`, `MessageContext.cs`, `IMessageHandler.cs`. Test `tests/Ledgerline.BuildingBlocks.Tests/EnvelopeTests.cs`; add a project reference from that test project to the Messaging project.
**Produces:** `MessageEnvelope(Guid MessageId, string Type, DateTimeOffset OccurredAt, Guid CorrelationId, Guid? CausationId, string? TraceParent, JsonElement Payload)`; `MessageTypeRegistry.NameOf<T>()`, `.NameOf(Type)`, `.TryResolve(string name, out Type type)`, `.Register(Type)`; `MessageTypeRegistry.ExchangeFor(string typeName)` returning `"ledgerline." + firstSegment`; `EnvelopeSerializer.Serialize(MessageEnvelope) : byte[]`, `.Deserialize(ReadOnlySpan<byte>) : MessageEnvelope`, `.ToPayload<T>(T)`, `.FromPayload(JsonElement, Type) : object`.
**Tests:** `Serialize_uses_camel_case_and_iso_timestamps` (compare with the JSON sample in the spec, field by field); `Deserialize_ignores_unknown_fields`; `Deserialize_tolerates_missing_optional_fields` (`causationId`, `traceParent`); `NameOf_reads_MessageType_attribute`; `NameOf_throws_for_type_without_attribute`; `ExchangeFor_uses_first_segment` (`"accounts.command.reserve-funds.v1"` gives `"ledgerline.accounts"`); `Type_name_must_match_pattern` (regex `^[a-z]+\.(command|event)\.[a-z0-9-]+\.v[0-9]+$`, `Register` throws otherwise).
**Implement:** `System.Text.Json`, `JsonSerializerDefaults.Web`, enums as strings.
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Tests --filter-class "*EnvelopeTests"`
**Commit:** `feat(messaging): add envelope, type registry and serializer`

### M1-T03: Shared test infrastructure

- [x] Done

**Spec:** `testing.md` section 2. **Depends:** none.
**Files:** Create `tests/Ledgerline.Testing/Ledgerline.Testing.csproj` (a library: set `IsTestProject=false`, `OutputType=Library` in the csproj), `InfrastructureFixture.cs`, `Eventually.cs`. Add to `Directory.Packages.props`: `Testcontainers.PostgreSql`, `Testcontainers.RabbitMq`, `Testcontainers.Redis` 4.16.0, `Npgsql` (version that `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 depends on), `RabbitMQ.Client` 7.2.2, `StackExchange.Redis` 3.4.35. Add the project to `Ledgerline.slnx`. Reference it from `tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests`. Test `tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests/InfrastructureFixtureTests.cs`.
**Produces:** `InfrastructureFixture : IAsyncLifetime` with `PostgresAdminConnectionString`, `RabbitMqUri`, `RedisConnectionString`, `Task StopRabbitMqAsync()`, `Task StartRabbitMqAsync()`, `Task<string> CreateDatabaseAsync(string prefix)` (returns a connection string to a fresh empty database); xunit assembly fixture registration helper; `Eventually.ShouldAsync(Func<Task<bool>> condition, TimeSpan? timeout = null, string? because = null)` polling every 50 ms, default 10 s, throwing `ShouldAssertException` on timeout.
**Tests:** `Postgres_accepts_connections`; `RabbitMq_accepts_connections`; `Redis_answers_ping`; `Rabbit_can_be_stopped_and_started` (connection fails after stop, succeeds after start, same port); `Eventually_times_out_with_message`.
**Implement:** image tags identical to `compose.yaml`. RabbitMQ must keep the same host port across stop and start: bind a fixed free port chosen at fixture start, do not rely on random mapping.
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests --filter-class "*InfrastructureFixtureTests"`
**Commit:** `test: add shared Testcontainers fixture and Eventually helper`

### M1-T04: Outbox and inbox tables, IOutbox

- [x] Done

**Spec:** `building-blocks.md` sections 5, 6; ADR 0003. **Depends:** M1-T02, M1-T03.
**Files:** Create in Messaging project: `Outbox/OutboxMessage.cs`, `Outbox/IOutbox.cs`, `Outbox/EfOutbox.cs`, `Inbox/InboxMessage.cs`, `ModelBuilderExtensions.cs`. Add packages `Microsoft.EntityFrameworkCore` 10.0.15, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3. Create `tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests/Support/TestDbContext.cs` (one entity `Widget(Guid Id, string Name)`, calls `AddOutboxAndInbox()`, schema created with `EnsureCreated`). Test `OutboxTests.cs`.
**Produces:** `IOutbox.Enqueue<T>(T message, Guid correlationId, Guid? causationId = null)`; `EfOutbox<TDbContext>`; `ModelBuilder.AddOutboxAndInbox()`; entity classes with the column names of the spec (snake_case via explicit `HasColumnName`).
**Tests:** `Enqueue_then_SaveChanges_writes_one_pending_row_with_type_and_payload`; `Enqueue_then_rollback_writes_nothing` (transaction rolled back, zero rows); `Enqueue_captures_current_trace_parent` (start an `Activity`, assert `trace_parent` equals `Activity.Current.Id`); `Schema_matches_spec` (query `information_schema.columns` for both tables and compare the column name sets with the spec).
**Implement:** `Enqueue` only adds an entity to the scoped `DbContext`; it never calls `SaveChanges`.
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests --filter-class "*OutboxTests"`
**Commit:** `feat(messaging): add outbox and inbox tables and IOutbox`

### M1-T05: RabbitMQ connection and topology

- [x] Done

**Spec:** `building-blocks.md` section 4. **Depends:** M1-T02, M1-T03.
**Files:** Create in Messaging project: `RabbitMq/RabbitMqOptions.cs`, `RabbitMq/RabbitMqConnection.cs` (singleton holding one `IConnection`, automatic recovery on), `RabbitMq/Topology.cs`. Test `TopologyTests.cs`.
**Produces:** `Topology.DeclareAsync(IChannel channel, string service, IReadOnlyCollection<string> consumedTypeNames, CancellationToken ct)`; static names `Topology.InboxQueue(service)`, `.RetryQueue(service)`, `.DeadQueue(service)`; `RabbitMqConnection.CreateChannelAsync(bool publisherConfirms, CancellationToken ct)`.
**Tests:** `Declare_creates_exchange_three_queues_and_bindings` (assert through passive declares and by publishing to the exchange and reading from the inbox queue); `Declare_is_idempotent` (twice, no exception); `Retry_queue_dead_letters_back_to_inbox_after_ttl` (publish to retry queue, message arrives in inbox within 7 s); `Queues_are_quorum` (management API or `x-queue-type` argument check via a conflicting passive declare).
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests --filter-class "*TopologyTests"`
**Commit:** `feat(messaging): declare RabbitMQ topology`

### M1-T06: Confirmed publisher and BrokerProbe

- [x] Done

**Spec:** `building-blocks.md` sections 2, 7. **Depends:** M1-T05.
**Files:** Create `RabbitMq/IOutboxPublisher.cs`, `RabbitMq/RabbitMqOutboxPublisher.cs` in Messaging; `tests/Ledgerline.Testing/BrokerProbe.cs`. Test `PublisherTests.cs`.
**Produces:** `IOutboxPublisher.PublishAsync(OutboxMessage message, CancellationToken ct)` (throws `PublishFailedException` when not confirmed within 5 s, when returned as unroutable, or when the connection is down); `BrokerProbe.CreateAsync(string rabbitUri, params string[] routingKeyPatterns)` (declares needed exchanges, an exclusive auto-delete queue, binds), `.Messages : IReadOnlyList<MessageEnvelope>`, `.WaitForAsync<T>(Func<T, bool> predicate, TimeSpan? timeout)`, `.PublishAsync<T>(T payload, Guid correlationId, Guid? messageId = null)`, `.PublishRawAsync(MessageEnvelope envelope)`.
**Tests:** `Publish_sets_amqp_properties_from_envelope` (`message_id`, `type`, `correlation_id`, `content_type`, persistent, `traceparent` header, `x-ledgerline-attempt = 1`); `Publish_to_exchange_without_binding_throws_PublishFailedException` (mandatory return); `Publish_when_broker_is_down_throws_PublishFailedException` (uses `StopRabbitMqAsync`).
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests --filter-class "*PublisherTests"`
**Commit:** `feat(messaging): add confirmed publisher and test probe`

### M1-T07: Outbox relay

- [x] Done

**Spec:** `building-blocks.md` section 7; ADR 0003. **Depends:** M1-T04, M1-T06.
**Files:** Create `Outbox/OutboxRelay.cs` (`BackgroundService`, generic on `TDbContext`), `Outbox/OutboxSignal.cs`, `Outbox/RelayOptions.cs`. Test `OutboxRelayTests.cs`.
**Produces:** `OutboxRelay<TDbContext>`; `OutboxSignal.Notify()` called by `EfOutbox` through a `SaveChanges` interceptor (`OutboxSaveChangesInterceptor`) after a successful commit that contained outbox rows; `ICrashPoint` with `void Hit(string name)` and `NoCrashPoint`; relay calls `Hit("relay.after-publish")` after each confirmed publish and before marking the row.
**Tests:** `Relay_publishes_pending_rows_in_sequence_order_and_marks_them_processed` (20 rows); `Relay_backs_off_and_records_error_when_publish_fails` (fake `IOutboxPublisher` failing 3 times: `attempts = 3`, `last_error` set, then delivered); `Relay_stops_batch_at_first_failure` (row 2 of 5 fails: rows 3 to 5 are not published before row 2); `Two_relays_do_not_publish_the_same_row_twice` (two hosted relays, 200 rows, probe sees 200 distinct `messageId`s and no duplicates); `Relay_reacts_to_signal_faster_than_poll_interval` (poll interval set to 10 s, message delivered within 1 s).
**Implement:** raw SQL `FOR UPDATE SKIP LOCKED` through `FromSql`. Backoff formula from the spec.
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests --filter-class "*OutboxRelayTests"`
**Commit:** `feat(messaging): add outbox relay`

### M1-T08: Consumer pipeline with inbox deduplication

- [x] Done

**Spec:** `building-blocks.md` section 8 steps 1 to 5. **Depends:** M1-T05, M1-T04.
**Files:** Create `Consuming/ConsumerHost.cs` (`BackgroundService`), `Consuming/HandlerRegistry.cs`, `Consuming/IAckStrategy.cs`, `Consuming/DefaultAckStrategy.cs`. Test `ConsumerTests.cs` with a test handler `RenameWidgetHandler` that updates a `Widget` row and enqueues `WidgetRenamed`.
**Produces:** `ConsumerHost<TDbContext>`; `HandlerRegistry.Add<TMessage, THandler>()`; `IAckStrategy.AckAsync(IChannel, ulong deliveryTag, CancellationToken)`; handler invocation inside an EF Core transaction that also holds the inbox insert and outbox rows.
**Tests:** `Message_is_handled_and_acknowledged` (state changed, queue empty); `Same_message_delivered_twice_is_handled_once` (same envelope twice: one state change, one outbox row, inbox has one row); `Same_message_delivered_concurrently_to_two_consumers_is_handled_once` (two `ConsumerHost` instances, 50 messages each published twice); `Handler_state_change_inbox_row_and_outbox_row_commit_atomically` (handler throws after changing state: no state change, no inbox row, no outbox row).
**Implement:** inbox insert with `ExecuteSqlInterpolatedAsync("INSERT ... ON CONFLICT DO NOTHING")` and check affected rows. Prefetch from options.
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests --filter-class "*ConsumerTests"`
**Commit:** `feat(messaging): add consumer pipeline with inbox`

### M1-T09: Retry queue and dead-lettering

- [x] Done

**Spec:** `building-blocks.md` section 8 steps 1 and 6. **Depends:** M1-T08.
**Files:** Modify `Consuming/ConsumerHost.cs`. Test `RetryAndDeadLetterTests.cs`.
**Tests:** `Failing_handler_is_retried_through_retry_queue_with_incremented_attempt` (handler fails twice then succeeds: `MessageContext.Attempt` values seen are 1, 2, 3); `Poison_message_goes_to_dead_queue_after_max_attempts` (`MaxAttempts = 3` in the test: dead queue has 1 message with header `x-ledgerline-attempt = 3`, inbox queue empty, no inbox row); `Unknown_message_type_is_dead_lettered_immediately` (header `x-ledgerline-dead-reason = "unknown-type"`); `Unparseable_body_is_dead_lettered_immediately` (`"invalid-envelope"`).
**Implement:** the retry queue TTL is 5 s in production; `Topology.DeclareAsync` takes the TTL from options so tests use 200 ms.
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests --filter-class "*RetryAndDeadLetterTests"`
**Commit:** `feat(messaging): add retry and dead-letter handling`

### M1-T10: Registration API and two-host round trip

- [x] Done

**Spec:** `building-blocks.md` section 5. **Depends:** M1-T07, M1-T09.
**Files:** Create `MessagingBuilder.cs`, `MessagingServiceCollectionExtensions.cs`. Test `RoundTripTests.cs` (two generic hosts, "ping" and "pong", each with its own database and `TestDbContext`).
**Produces:** `services.AddLedgerlineMessaging<TDbContext>(configuration, m => m.Service("x").Consume<TMsg, THandler>())` registering connection, topology initializer (runs before the consumer starts), relay, consumer, `IOutbox`, `ICrashPoint` (`NoCrashPoint`), `IAckStrategy`, cleanup job `MessagingRetentionJob` (deletes processed outbox rows and inbox rows older than `Retention:Days`, runs hourly).
**Tests:** `Ping_from_one_service_produces_pong_from_the_other` (correlation ID preserved, `causationId` of pong equals `messageId` of ping); `Host_stops_gracefully_with_in_flight_handler` (handler blocks 1 s, `StopAsync` waits, effect committed, message acknowledged); `Retention_job_deletes_only_old_processed_rows`.
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests --filter-class "*RoundTripTests"`
**Commit:** `feat(messaging): add registration API and retention job`

### M1-T11: Messaging failure proofs (broker down, crash, duplicate)

- [x] Done

**Spec:** `testing.md` F1, F2, F3 (Messaging rows). **Depends:** M1-T10.
**Files:** Create `tests/Ledgerline.Testing/Faults/{StoppingCrashPoint.cs,DroppingAckStrategy.cs}`. Test `tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests/FailureScenarioTests.cs`.
**Produces:** `StoppingCrashPoint(string name, IHostApplicationLifetime)`: on the first `Hit(name)` it aborts the host (`Environment.FailFast` is not usable in-process, so: mark crashed, throw `HostAbortedException`, and make every later relay or consumer step of that host a no-op) and exposes `Task Crashed`; `DroppingAckStrategy`: skips the acknowledgment once and closes the channel.
**Tests (exact names from `testing.md`):** `Outbox_keeps_messages_while_broker_is_down_and_delivers_after_recovery`; `Crash_after_publish_before_marking_processed_republishes_and_consumer_deduplicates`; `Crash_after_commit_before_ack_redelivers_and_inbox_absorbs_it` (uses `DroppingAckStrategy`).
**Implement:** only what the tests force in the messaging layer (typically channel re-creation after connection loss in relay and consumer).
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests --filter-class "*FailureScenarioTests"`
**Commit:** `test(messaging): prove broker outage, crash and duplicate handling`

### M1-T12: Web defaults (problem details, dev auth, health)

- [x] Done

**Spec:** `building-blocks.md` section 9. **Depends:** none.
**Files:** Create in `src/BuildingBlocks/Ledgerline.BuildingBlocks.Web/`: `WebDefaults.cs`, `Problems/ProblemCodes.cs`, `Problems/ProblemResults.cs`, `Auth/DevHeaderAuthenticationHandler.cs`, `Auth/CurrentUser.cs`, `Auth/Policies.cs`, `Health/HealthEndpoints.cs`, `Pagination/Page.cs`. Add `Microsoft.AspNetCore.Mvc.Testing` 10.0.15. Reference Web and Mvc.Testing from `tests/Ledgerline.BuildingBlocks.Tests`; test `WebDefaultsTests.cs` using a minimal in-test `WebApplication`.
**Produces:** `builder.AddLedgerlineWebDefaults(string serviceName)`; `app.UseLedgerlineWebDefaults()` (exception handler to problem details, authentication, authorization, `/health/live`, `/health/ready`); `ProblemResults.NotFound()`, `.InvalidState(string detail)`, `.Validation(IDictionary<string,string[]> errors)`, `.IdempotencyKeyMissing()`, `.IdempotencyKeyReused()`; `ICurrentUser { Guid Id; string? Email; bool IsInRole(string) }`; policy names `Policies.Customer`, `.Merchant`, `.Admin`; `Page<T>(IReadOnlyList<T> Items, string? NextCursor)`. Dev auth: when `Auth:Mode = "DevHeaders"`, headers `X-Dev-User` (UUID), `X-Dev-Roles` (comma separated), `X-Dev-Email` create the principal. The handler refuses to register unless the environment is `Development` or `Testing`.
**Tests:** `Unhandled_exception_becomes_problem_details_with_trace_id`; `Missing_dev_user_header_gives_401`; `Role_policy_denies_with_403`; `Dev_auth_throws_at_startup_in_Production_environment`; `Health_live_returns_200_and_ready_reflects_checks`.
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Tests --filter-class "*WebDefaultsTests"`
**Commit:** `feat(web): add problem details, dev auth and health endpoints`

### M1-T13: Accounts database, migration, system accounts, migrate command

- [x] Done

**Spec:** `accounts.md` sections 2, 5, 10. **Depends:** M1-T04, M1-T12.
**Files:** Create in Accounts project: `Infrastructure/AccountsDbContext.cs`, `Infrastructure/Entities/*.cs` (one per table), `Infrastructure/Migrations/` (EF migration `Initial`), `Infrastructure/SystemAccounts.cs`, `Infrastructure/MigrateCommand.cs`; modify `Program.cs` (web defaults, DbContext, `args[0] == "migrate"` branch). Create `tests/Ledgerline.Testing/ServiceHost.cs`, `DatabaseFactory.cs`. Reference `Ledgerline.Testing` from `Ledgerline.Accounts.IntegrationTests`. Test `MigrationTests.cs`.
**Produces:** `SystemAccounts.IdFor(string code, string currency) : Guid` (UUIDv5, namespace from the spec); `SystemAccounts.BankClearing`, `.PaymentsPending` code constants; `ServiceHost<TProgram>.StartAsync(InfrastructureFixture, string databasePrefix, Action<IServiceCollection>? overrides = null)` exposing `Services`, `CreateClient(Guid? userId = null, string roles = "customer", string? email = null)`, `ConnectionString`; `DatabaseFactory.CreateMigratedAsync<TDbContext>(...)` using a template database per service.
**Tests:** `Migration_creates_all_tables_from_spec` (table and column names for all seven tables); `Migration_seeds_system_accounts_for_each_configured_currency` (2 codes times 3 currencies, deterministic IDs, type `System`, balance 0, one `account-opened.v1` event each with version 1); `Migrate_is_idempotent`; `Wallet_check_constraint_rejects_negative_balance` (raw UPDATE fails with `23514`); `SystemAccounts_IdFor_is_stable` (hard-code the expected GUID for `("BANK_CLEARING","TRY")` after computing it once).
**Prove:** `dotnet test --project tests/Ledgerline.Accounts.IntegrationTests --filter-class "*MigrationTests"`
**Commit:** `feat(accounts): add schema, system account seed and migrate command`

### M1-T14: Ledger entry builders and validation (pure)

- [x] Done

**Spec:** `design.md` section 8; `accounts.md` section 6 step 2. **Depends:** M1-T01.
**Files:** Create in Accounts project: `Ledger/Direction.cs`, `Ledger/EntryType.cs`, `Ledger/PostingLine.cs`, `Ledger/PostingRequest.cs`, `Ledger/PostingResult.cs`, `Ledger/PostingValidator.cs`, `Ledger/Entries.cs`. Test `tests/Ledgerline.Accounts.Tests/{PostingValidatorTests.cs,EntriesTests.cs}`.
**Produces:** the records from `accounts.md` section 6; `PostingValidator.EnsureWellFormed(PostingRequest)` throwing `MalformedPostingException`; `Entries.TopUp(opId, paymentId, walletId, Money)`, `.Transfer(opId, paymentId, fromId, toId, Money)`, `.Hold(opId, paymentId, walletId, Money)`, `.Capture(opId, paymentId, customerId, merchantId, long holdMinor, long captureMinor, string currency)`, `.Release(opId, paymentId, customerId, Money)`, `.Refund(opId, paymentId, merchantId, customerId, Money)`, each returning a `PostingRequest`.
**Tests:** validator: `Rejects_single_line`, `Rejects_unbalanced_lines`, `Rejects_non_positive_amount`, `Rejects_same_account_twice`. Builders: one test per entry type asserting the exact lines of the design table, plus `Capture_with_full_amount_has_two_lines`, `Capture_with_partial_amount_credits_remainder_to_customer`, `Capture_and_Release_skip_status_check_for_customer`.
**Prove:** `dotnet test --project tests/Ledgerline.Accounts.Tests`
**Commit:** `feat(accounts): add ledger entry builders and validation`

### M1-T15: Ledger posting core

- [x] Done

**Spec:** `accounts.md` section 6 steps 3 to 6, section 7; ADR 0007. **Depends:** M1-T13, M1-T14.
**Files:** Create `Ledger/ILedger.cs`, `Ledger/Ledger.cs`, `Ledger/EventStore.cs`, `Ledger/LedgerEvents.cs`; `tests/Ledgerline.Testing/LedgerAssert.cs`. Test `tests/Ledgerline.Accounts.IntegrationTests/LedgerPostingTests.cs` (tests insert wallets directly through a small test helper `TestAccounts.OpenWalletAsync(db, ownerId, currency, type)` that appends the `account-opened.v1` event and projection row).
**Produces:** `ILedger.PostAsync(PostingRequest, CancellationToken) : Task<PostingResult>` (requires an ambient transaction on the scoped `AccountsDbContext`; does not commit); `LedgerAssert.InvariantsHoldAsync(string accountsConnectionString)` implementing the four checks of ADR 0008 item 6 (the replay check re-folds `events` in memory and compares with `accounts`, `journal_lines`).
**Tests:** `TopUp_credits_wallet_and_debits_bank_clearing`; `Transfer_moves_money_between_wallets`; `Transfer_with_insufficient_funds_is_rejected_and_changes_nothing`; `Posting_to_frozen_account_is_rejected_AccountFrozen`; `Posting_with_wrong_currency_is_rejected_CurrencyMismatch`; `Posting_to_unknown_account_is_rejected_AccountNotFound`; `Posting_appends_one_event_per_line_with_next_version`; `Posting_writes_journal_entry_and_lines_with_balance_after`. Every test ends with `LedgerAssert.InvariantsHoldAsync`.
**Implement:** lock query `SELECT * FROM accounts WHERE id = ANY({ids}) ORDER BY id FOR UPDATE` via `FromSql`. Checks in the order of the spec. Hold handling is added in M2-T13; leave `holds` untouched here.
**Prove:** `dotnet test --project tests/Ledgerline.Accounts.IntegrationTests --filter-class "*LedgerPostingTests"`
**Commit:** `feat(accounts): add event-sourced posting core`

### M1-T16: Accounts contracts and command handlers (top-up, transfer)

- [x] Done

**Spec:** `accounts.md` sections 4, 6 step 1, 8. **Depends:** M1-T10, M1-T15.
**Files:** Create `src/Contracts/Ledgerline.Contracts/Accounts/{CreditTopUp.cs,TransferFunds.cs,LedgerOperationSucceeded.cs,LedgerOperationRejected.cs,JournalEntryPosted.cs,LedgerOperationKind.cs,LedgerRejectionReasons.cs}`; make Contracts reference `Ledgerline.BuildingBlocks.Messaging` (only for `MessageTypeAttribute`, as the spec allows). Create in Accounts: `Features/LedgerCommands/{CreditTopUpHandler.cs,TransferFundsHandler.cs,LedgerOperationRunner.cs}`; modify `Program.cs` (`AddLedgerlineMessaging<AccountsDbContext>`, service name `accounts`). Tests: `tests/Ledgerline.BuildingBlocks.Tests/Contracts/ContractSampleTests.cs` with JSON samples in `Contracts/Samples/*.json`; `tests/Ledgerline.Accounts.IntegrationTests/LedgerCommandTests.cs`.
**Produces:** records with the payload fields of the spec, each with `[MessageType("...")]`; `LedgerOperationRunner.RunAsync(Guid operationId, Guid paymentId, LedgerOperationKind kind, Func<PostingRequest> build, MessageContext context, CancellationToken ct)` doing: lookup in `ledger_operations`, replay stored reply or post, store outcome, enqueue reply and `JournalEntryPosted`; calls `ICrashPoint.Hit("ledger.after-commit")` from the pipeline's post-commit hook (add `IPostCommitHook` to the consumer pipeline if not present).
**Tests:** contract: `Every_contract_type_round_trips_its_sample` (theory over all `[MessageType]` types in the Contracts assembly; fails if a type has no sample file); `Every_contract_type_name_is_unique`. Integration: `CreditTopUp_command_posts_entry_and_publishes_succeeded_and_journal_events` (via `BrokerProbe`); `TransferFunds_with_insufficient_funds_publishes_rejected`; `Same_operation_with_new_message_id_returns_same_reply_without_second_entry` (exact name from `testing.md`); `Same_message_id_twice_posts_once`.
**Prove:** `dotnet test --project tests/Ledgerline.Accounts.IntegrationTests --filter-class "*LedgerCommandTests"` and `dotnet test --project tests/Ledgerline.BuildingBlocks.Tests --filter-class "*ContractSampleTests"`
**Commit:** `feat(accounts): handle top-up and transfer ledger commands`

### M1-T17: Accounts HTTP API

- [x] Done

**Spec:** `accounts.md` section 3 (all except freeze, unfreeze, trial balance); `building-blocks.md` section 9. **Depends:** M1-T16.
**Files:** Create `src/Contracts/Ledgerline.Contracts/Accounts/AccountOpened.cs`; in Accounts `Features/Accounts/{OpenAccountEndpoint.cs,GetAccountEndpoint.cs,ListAccountsEndpoint.cs,ListEntriesEndpoint.cs,AccountResponse.cs}`; modify `Program.cs`. Test `tests/Ledgerline.Accounts.IntegrationTests/AccountsApiTests.cs`.
**Tests:** `Open_account_returns_201_with_zero_balance_and_publishes_AccountOpened`; `Open_account_without_idempotency_key_returns_400`; `Customer_cannot_open_merchant_wallet` (400); `Second_account_of_same_type_and_currency_returns_409`; `Get_foreign_account_returns_404`; `List_entries_is_newest_first_and_paginates` (5 entries, `limit=2`, follow cursors, 3 pages); `Unsupported_currency_returns_400`.
**Implement:** minimal API endpoints mapped in one `MapAccountsEndpoints()` extension. Idempotency for this endpoint is database-only in M1 (unique constraint); the Redis fast path arrives in M2-T02.
**Prove:** `dotnet test --project tests/Ledgerline.Accounts.IntegrationTests --filter-class "*AccountsApiTests"`
**Commit:** `feat(accounts): add account HTTP API`

### M1-T18: HTTP idempotency (database guarantee)

- [x] Done

**Spec:** `building-blocks.md` section 9, steps 1 and 3. **Depends:** M1-T17.
**Files:** Create `src/BuildingBlocks/Ledgerline.BuildingBlocks.Web/Idempotency/{IdempotencyKey.cs,RequestHash.cs,IdempotencyEndpointFilter.cs}`; modify `OpenAccountEndpoint.cs` to use them. Test `tests/Ledgerline.BuildingBlocks.Tests/RequestHashTests.cs`, extend `AccountsApiTests.cs`.
**Produces:** `IdempotencyKey.TryRead(HttpContext, out string key)` (validates the pattern); `RequestHash.Compute(string method, string path, ReadOnlySpan<byte> body) : string` (canonical JSON: properties sorted, no whitespace); endpoint filter `RequireIdempotencyKey()` that rejects a missing or malformed key and stores key and hash in `HttpContext.Items`; helper `IdempotentCreate.ExecuteAsync(...)` that catches the unique violation (`23505` on the idempotency constraint), loads the existing row, compares hashes, and returns the existing resource or `422`.
**Tests:** `Hash_is_independent_of_property_order_and_whitespace`; `Hash_differs_for_different_path`; API: `Same_key_same_body_returns_same_account_with_201`; `Same_key_different_body_returns_422`; `Malformed_key_returns_400` (65 characters, and a key with a space); `Ten_concurrent_identical_requests_create_one_account`.
**Prove:** `dotnet test --project tests/Ledgerline.Accounts.IntegrationTests --filter-class "*AccountsApiTests"` and `dotnet test --project tests/Ledgerline.BuildingBlocks.Tests --filter-class "*RequestHashTests"`
**Commit:** `feat(web): add idempotency key handling with database guarantee`

### M1-T19: Ledger concurrency proofs and projection rebuild

- [x] Done

**Spec:** `testing.md` F4 (first three rows) and F5 last row; `accounts.md` section 10. **Depends:** M1-T16.
**Files:** Create in Accounts `Infrastructure/RebuildProjectionsCommand.cs`; modify `Program.cs` (`rebuild-projections` argument). Test `tests/Ledgerline.Accounts.IntegrationTests/{ConcurrencyTests.cs,RebuildTests.cs}`.
**Tests (exact names):** `Concurrent_debits_never_overdraw`; `Opposite_direction_transfers_do_not_deadlock`; `Concurrent_postings_keep_stream_versions_gapless`; `Projections_rebuilt_from_events_equal_live_projections` (dump the four projection tables ordered by key before and after the rebuild and compare).
**Implement:** tests publish commands through `BrokerProbe` with consumer prefetch 16 and, for the second test, two `ServiceHost` instances on the same database. Fix what the tests reveal. If PostgreSQL reports `40P01`, the lock ordering is wrong: fix the ordering, do not add a retry.
**Prove:** `dotnet test --project tests/Ledgerline.Accounts.IntegrationTests --filter-class "*ConcurrencyTests"` and `--filter-class "*RebuildTests"`
**Commit:** `test(accounts): prove concurrent postings and projection rebuild`

### M1-T20: Payments saga core for M1 (pure)

- [x] Done

**Spec:** `payments.md` sections 2, 4.2, 7. M1 simplification: no `RiskCheck`, and `TopUp` has no bank steps. **Depends:** M1-T16.
**Files:** Create in Payments: `Sagas/{PaymentStatus.cs,SagaStep.cs,PaymentKind.cs,PaymentSnapshot.cs,SagaInput.cs,SagaEffect.cs,SagaDecision.cs,PaymentSaga.cs,OperationIds.cs}`. Test `tests/Ledgerline.Payments.Tests/{PaymentSagaTests.cs,OperationIdsTests.cs}`.
**Produces:** the types of `payments.md` section 7 with all enum members of section 2 declared now; `PaymentSaga.Start(PaymentSnapshot payment, DateTimeOffset now) : SagaDecision` and `PaymentSaga.Decide(PaymentSnapshot, SagaInput, DateTimeOffset) : SagaDecision`; `OperationIds.For(Guid paymentId, SagaStep step) : Guid` (UUIDv5); `PaymentSagaOptions { bool RiskEnabled; bool BankEnabled; }` passed to both methods so M2 turns the missing steps on without changing M1 tests (`RiskEnabled = false`, `BankEnabled = false` here).
**Tests:** `Transfer_start_sends_TransferFunds_and_schedules_30s_timeout`; `Transfer_ledger_succeeded_completes_and_publishes_PaymentCompleted`; `Transfer_ledger_rejected_fails_with_reason_and_publishes_PaymentFailed`; `TopUp_start_without_bank_sends_CreditTopUp`; `TopUp_ledger_succeeded_completes`; `Decide_ignores_inputs_in_final_states` (theory: every final status times every input gives `Ignored = true`, no effects); `Decide_ignores_reply_for_other_step`; `StepTimedOut_resends_same_command_with_same_operation_id`; `OperationIds_are_deterministic_and_differ_per_step`.
**Prove:** `dotnet test --project tests/Ledgerline.Payments.Tests`
**Commit:** `feat(payments): add pure saga core for transfer and simple top-up`

### M1-T21: Payments persistence, account replica, HTTP API, reply handlers

- [x] Done

**Spec:** `payments.md` sections 3 (topups, transfers, GET), 5, 6. **Depends:** M1-T18, M1-T20.
**Files:** Create `src/Contracts/Ledgerline.Contracts/Payments/{PaymentCompleted.cs,PaymentFailed.cs}`; in Payments: `Infrastructure/PaymentsDbContext.cs`, `Infrastructure/Entities/*.cs`, `Infrastructure/Migrations/Initial`, `Infrastructure/MigrateCommand.cs`, `Sagas/SagaRunner.cs`, `Features/AccountReplica/{AccountOpenedHandler.cs}`, `Features/Topups/CreateTopUpEndpoint.cs`, `Features/Transfers/CreateTransferEndpoint.cs`, `Features/Payments/{GetPaymentEndpoint.cs,PaymentResponse.cs}`, `Features/LedgerReplies/{LedgerOperationSucceededHandler.cs,LedgerOperationRejectedHandler.cs}`; modify `Program.cs`. Reference `Ledgerline.Testing` from `Ledgerline.Payments.IntegrationTests`. Test `tests/Ledgerline.Payments.IntegrationTests/{PaymentsApiTests.cs,TransferFlowTests.cs}`. In these tests `BrokerProbe` plays Accounts.
**Produces:** `SagaRunner.StartAsync(Payment)` and `SagaRunner.ApplyAsync(Guid paymentId, SagaInput input, MessageContext? context, CancellationToken ct)`: load with `xmin` concurrency, call `PaymentSaga`, apply status, step, reason, write `saga_steps`, turn effects into outbox rows and `saga_timeouts` rows; calls `ICrashPoint.Hit("saga.after-transition-commit")` post-commit. Schema: all tables and columns of section 6 now (including columns only used from M2 on).
**Tests:** API: `Post_transfer_returns_202_with_location_and_processing_status`; `Post_with_invalid_amount_returns_400` (theory: 0, -1, `1_000_000_000_01`); `Transfer_from_unknown_account_returns_404`; `Transfer_from_foreign_account_returns_404`; `Transfer_to_same_account_returns_400`; `Transfer_with_currency_mismatch_returns_400`; `Same_idempotency_key_returns_same_payment`; `Same_idempotency_key_sent_concurrently_creates_one_payment` (exact name; accept `202` only in M1 since the Redis in-progress marker arrives in M2). Flow: `AccountOpened_event_creates_account_ref`; `Transfer_completes_when_ledger_succeeds` (probe receives `transfer-funds`, replies succeeded, payment `Completed`, `payment-completed` published, timeline has 2 rows); `Transfer_fails_when_ledger_rejects`; `Duplicate_and_late_replies_do_not_move_a_final_payment` (exact name).
**Prove:** `dotnet test --project tests/Ledgerline.Payments.IntegrationTests`
**Commit:** `feat(payments): add payment API, saga runner and ledger reply handling`

### M1-T22: Saga step timeouts

- [x] Done

**Spec:** `payments.md` section 4.6 (ledger steps only in M1). **Depends:** M1-T21.
**Files:** Create in Payments `Sagas/SagaTimeoutPoller.cs` (`BackgroundService`), `Sagas/TimeoutOptions.cs`. Test `tests/Ledgerline.Payments.IntegrationTests/TimeoutTests.cs` (options shortened: step timeout 300 ms, poll 50 ms).
**Tests:** `Unanswered_ledger_step_is_resent_with_same_operation_id` (probe sees two `transfer-funds` with equal `operationId`, different `messageId`); `Ledger_step_moves_to_ManualReview_after_max_attempts` (5 sends, then `ManualReview`, `payment-failed` with `status = ManualReview`); `Answered_step_deletes_its_timeout_row`; `Two_pollers_do_not_resend_the_same_timeout_twice`.
**Implement:** poller feeds `StepTimedOut` into `SagaRunner.ApplyAsync`. The "attempt consumed only after the re-sent command was relayed" rule is M2-T17; here every due timeout counts.
**Prove:** `dotnet test --project tests/Ledgerline.Payments.IntegrationTests --filter-class "*TimeoutTests"`
**Commit:** `feat(payments): add saga step timeouts`

### M1-T23: Crash proofs for Accounts and Payments

- [x] Done

**Spec:** `testing.md` F2 rows 1 and 2. **Depends:** M1-T11, M1-T21.
**Files:** Test `tests/Ledgerline.Accounts.IntegrationTests/CrashTests.cs`, `tests/Ledgerline.Payments.IntegrationTests/CrashTests.cs`.
**Tests (exact names):** `Crash_after_ledger_commit_before_ack_does_not_double_post`; `Crash_after_saga_transition_commit_resumes_from_outbox`. Pattern: start `ServiceHost` with `StoppingCrashPoint` for the named point, drive the flow until `Crashed` completes, dispose the host, start a second `ServiceHost` on the same database, assert completion and invariants.
**Prove:** `dotnet test --project tests/Ledgerline.Accounts.IntegrationTests --filter-class "*CrashTests"` and `dotnet test --project tests/Ledgerline.Payments.IntegrationTests --filter-class "*CrashTests"`
**Commit:** `test: prove crash recovery in accounts and payments`

### M1-T24: Dockerfiles and the one command

- [x] Done

**Spec:** ADR 0005; `design.md` section 11 (migrations). **Depends:** M1-T21.
**Files:** Create `src/Services/Accounts/Ledgerline.Accounts/Dockerfile`, `src/Services/Payments/Ledgerline.Payments/Dockerfile` (multi-stage, `mcr.microsoft.com/dotnet/sdk:10.0` and `aspnet:10.0`, non-root user, build context = repository root), `.dockerignore`; modify `compose.yaml`: under profile `app` add `accounts-migrate`, `payments-migrate` (run `migrate`, `restart: "no"`), `accounts`, `payments` (depend on infrastructure `service_healthy` and their migrate job `service_completed_successfully`; environment variables for connection strings, RabbitMQ URI, `Auth__Mode=DevHeaders`, `ASPNETCORE_ENVIRONMENT=Development`; host ports 5101 and 5102; healthcheck on `/health/ready`). Create `scripts/smoke.sh` and `scripts/smoke.ps1` (open two wallets with dev headers, top up 100.00, transfer 25.00, poll until `Completed`, print balances, exit non-zero on mismatch).
**Tests:** the smoke script is the test.
**Prove:** `docker compose --profile app up --build -d --wait && ./scripts/smoke.sh` prints `SMOKE OK: alice=7500 bob=2500` and exits 0. Then `docker compose --profile app down`.
**Commit:** `feat: run accounts and payments with one compose command`

### M1-T25: README for M1 and milestone check

- [x] Done

**Depends:** M1-T24.
**Files:** Modify `README.md`: fill "Quick start" with the verified commands, set the status table rows for M1, add the architecture diagram of the two services, list what is not there yet. Modify `.github/workflows/ci.yml`: nothing new needed (integration tests already run in `dotnet test`); confirm the job passes in under 10 minutes locally with `time dotnet test --solution Ledgerline.slnx`.
**Prove:** `dotnet test --solution Ledgerline.slnx` passes with zero failures; `grep -c $'\u2014' README.md docs/*.md docs/*/*.md` prints only zeros.
**Commit:** `docs: update README for milestone 1`

## Milestone 1 checklist

- [x] Starts with one command: `docker compose --profile app up --build -d` (M1-T24). **DoD: one command**
- [x] Outbox proven by tests: `OutboxTests`, `OutboxRelayTests` (M1-T04, M1-T07). **DoD: outbox proven**
- [x] Idempotent consumer proven by tests: `ConsumerTests`, `LedgerCommandTests` (M1-T08, M1-T16). **DoD: idempotent consumer proven**
- [x] Broker-down, crash, and duplicate tests pass at integration level (M1-T11, M1-T23). **DoD: three failure tests** (integration level; system level in M3)
- [x] Concurrent transfers proven (M1-T19).
- [x] ADRs 0000 to 0009 exist and match the code. **DoD: ADRs written**
- [x] README has quick start and status (M1-T25).
- [x] M1 PR #1 and merged main passed hosted build-test; GitGuardian passed after development-credential triage.

---

# Milestone 2: saga with compensation, Risk, Notifications, gateway, auth

### M2-T01: Keycloak realm and JWT authentication

- [x] Done

**Spec:** ADR 0004; `building-blocks.md` section 9 (Auth). **Depends:** M1 complete.
**Files:** Modify `deploy/compose/keycloak/ledgerline-realm.json` (clients `ledgerline-api` bearer-only, `ledgerline-k6` with direct access grants; audience mapper adding `ledgerline-api` to tokens of `ledgerline-web` and `ledgerline-k6`; realm roles in the `roles` claim; users `alice`, `bob` (customer), `shop` (merchant), `admin` with emails `<name>@example.test` and development passwords). Create in Web: `Auth/JwtAuthExtensions.cs`, `Auth/ClaimsCurrentUser.cs`; delete `Auth/DevHeaderAuthenticationHandler.cs`. Add `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.15. Create `tests/Ledgerline.Testing/TestTokens.cs`; modify `ServiceHost.CreateClient` to send a bearer token. Modify `compose.yaml` service environments (`Auth__Authority=http://keycloak:8081/realms/ledgerline`, `Auth__Audience=ledgerline-api`, `Auth__RequireHttpsMetadata=false`). Modify `scripts/smoke.*` to obtain tokens from Keycloak through `ledgerline-k6`. Test: modify `tests/Ledgerline.BuildingBlocks.Tests/WebDefaultsTests.cs`.
**Produces:** `Auth:Mode` values `Oidc` (authority and audience) and `TestKey` (symmetric key from `Auth:TestSigningKey`, allowed only in environment `Testing`); `TestTokens.Create(Guid userId, string roles, string? email)`.
**Tests:** `Request_without_token_gives_401`; `Token_with_wrong_audience_gives_401`; `Expired_token_gives_401`; `Roles_claim_maps_to_policies`; `TestKey_mode_throws_at_startup_outside_Testing_environment`; `No_dev_header_scheme_is_registered` (the `X-Dev-User` header alone gives 401). All existing integration tests still pass.
**Prove:** `dotnet test --solution Ledgerline.slnx` and `docker compose --profile app up --build -d --wait && ./scripts/smoke.sh`
**Commit:** `feat(auth): validate Keycloak JWTs and remove dev header auth`

### M2-T02: Redis fast path for idempotency

- [x] Done

**Spec:** `building-blocks.md` section 9 steps 2 and 4. **Depends:** M2-T01.
**Files:** Create in Web `Idempotency/RedisIdempotencyStore.cs`, `Idempotency/IIdempotencyStore.cs`; modify `IdempotencyEndpointFilter.cs`, `WebDefaults.cs` (register `IConnectionMultiplexer` with `AbortOnConnectFail = false`). Test `tests/Ledgerline.Payments.IntegrationTests/IdempotencyTests.cs`.
**Produces:** `IIdempotencyStore.TryBeginAsync(string scope, string key, string hash) : Task<IdempotencyBegin>` (`Started`, `InProgress`, `Done(resourceId)`, `HashMismatch`, `Unavailable`); `.CompleteAsync(scope, key, hash, resourceId)`; `.ReleaseAsync(scope, key)` on failure.
**Tests:** `Second_request_while_first_in_progress_returns_409_with_retry_after`; `Replay_after_completion_is_served_and_returns_same_id`; `Same_key_different_body_returns_422_from_redis_path`; `Redis_down_idempotency_falls_back_to_database` (exact name: stop Redis through the fixture, 5 identical POSTs give one payment); `Failed_request_releases_the_key` (validation error, then a corrected request with the same key succeeds).
**Prove:** `dotnet test --project tests/Ledgerline.Payments.IntegrationTests --filter-class "*IdempotencyTests"`
**Commit:** `feat(web): add Redis fast path for idempotency keys`

### M2-T03: Risk rules (pure)

- [x] Done

**Spec:** `risk.md` section 2. **Depends:** M1-T01.
**Files:** Create in Risk `Domain/{RiskRule.cs,RuleCode.cs,RiskRequest.cs,RiskCounters.cs,RiskEvaluator.cs,RiskOutcome.cs}`. Test `tests/Ledgerline.Risk.Tests/RiskEvaluatorTests.cs`.
**Produces:** `RiskEvaluator.Evaluate(IReadOnlyList<RiskRule> rules, RiskRequest request, RiskCounters counters) : RiskOutcome` where `RiskCounters(long DailyTotalMinor, int VelocityCount)` and `RiskOutcome(bool Approved, IReadOnlyList<string> RuleCodes)`.
**Tests:** `Approves_when_no_rules_enabled`; `MaxSingleAmount_rejects_above_threshold_and_allows_equal`; `MaxSingleAmount_with_kind_applies_only_to_that_kind`; `DailyAmountLimit_rejects_when_total_plus_amount_exceeds`; `VelocityLimit_rejects_when_count_exceeds_max`; `Reports_all_failing_rules`; `Rule_for_other_currency_is_ignored`; `Disabled_rule_is_skipped`.
**Prove:** `dotnet test --project tests/Ledgerline.Risk.Tests --filter-class "*RiskEvaluatorTests"`
**Commit:** `feat(risk): add rule evaluation`

### M2-T04: Risk service: schema, evaluate handler, daily totals

- [x] Done

**Spec:** `risk.md` sections 3, 4 (steps 1, 2, 4, 5, 6), 6, 7. **Depends:** M2-T03, M1-T10.
**Files:** Create `src/Contracts/Ledgerline.Contracts/Risk/{EvaluateRisk.cs,RiskApproved.cs,RiskRejected.cs}` plus samples; in Risk `Infrastructure/{RiskDbContext.cs,Entities/*.cs,Migrations/Initial,MigrateCommand.cs,RuleCache.cs}`, `Features/Evaluate/EvaluateRiskHandler.cs`, `Features/Evaluate/IVelocityCounter.cs`, `Features/Evaluate/DatabaseVelocityCounter.cs`; modify `Program.cs`. Create `tests/Ledgerline.Risk.IntegrationTests/` project (add to solution; `tests/Directory.Build.props` applies) with `EvaluateRiskTests.cs`.**Produces:** `IVelocityCounter.CountAsync(Guid customerId, Guid paymentId, DateTimeOffset at, int windowSeconds, CancellationToken ct) : Task<int>`; seeded rules with the spec defaults for `TRY`, `EUR`, `USD`.
**Tests:** `Approved_request_publishes_RiskApproved_and_adds_to_daily_total`; `Rejected_request_publishes_rule_codes_and_does_not_add_to_total`; `Evaluating_same_payment_twice_counts_once` (exact name); `Concurrent_evaluations_respect_daily_limit` (exact name: limit fits 3 of 10); `Daily_total_equals_sum_of_approved_decisions` (after 50 random evaluations).
**Prove:** `dotnet test --project tests/Ledgerline.Risk.IntegrationTests --filter-class "*EvaluateRiskTests"`
**Commit:** `feat(risk): evaluate payments against limits`

### M2-T05: Velocity windows in Redis with database fallback, Risk admin API

- [x] Done

**Spec:** `risk.md` section 4 step 3, section 5, section 8. **Depends:** M2-T04, M2-T01.
**Files:** Create in Risk `Features/Evaluate/RedisVelocityCounter.cs` (Lua script as an embedded string), `Features/Evaluate/FallbackVelocityCounter.cs` (tries Redis, falls back), `Features/Rules/{ListRulesEndpoint.cs,UpdateRuleEndpoint.cs,GetDecisionEndpoint.cs}`. Test `tests/Ledgerline.Risk.IntegrationTests/{VelocityTests.cs,RiskApiTests.cs}`.
**Tests:** `Velocity_counts_distinct_payments_within_window`; `Velocity_ignores_same_payment_added_twice`; `Velocity_forgets_entries_older_than_window` (pass explicit timestamps; no waiting); `Redis_down_velocity_falls_back_to_database` (exact name); `Update_rule_requires_admin` (403 for customer); `Update_rule_rejects_fields_that_do_not_apply` (400); `Updated_rule_takes_effect_after_cache_expiry` (cache TTL shortened to 100 ms in the test); `Get_decision_returns_404_for_unknown_payment`.
**Prove:** `dotnet test --project tests/Ledgerline.Risk.IntegrationTests`
**Commit:** `feat(risk): add Redis velocity windows and admin API`

### M2-T06: Bank Simulator: operations with idempotency

- [x] Done

**Spec:** `bank-simulator.md` sections 1, 2, 3 (authorizations, capture, void, refund, operations lookup), 4. **Depends:** M1-T12.
**Files:** Create in BankSimulator `Infrastructure/{BankDbContext.cs,Entities/*.cs,Migrations/Initial,MigrateCommand.cs}`, `Features/{AuthorizeEndpoint.cs,CaptureEndpoint.cs,VoidEndpoint.cs,RefundEndpoint.cs,LookupEndpoint.cs,ApiKeyFilter.cs,BankIdempotency.cs}`; modify `Program.cs`. Reference `Ledgerline.Testing` from `Ledgerline.BankSimulator.Tests`. Test `tests/Ledgerline.BankSimulator.Tests/BankOperationsTests.cs`.
**Tests:** `Authorize_with_tok_ok_returns_201`; `Authorize_with_tok_declined_returns_402_with_code`; `Unknown_token_returns_400`; `Capture_requires_authorized_state` (409 after void); `Capture_of_tok_capture_fails_returns_402`; `Refund_requires_captured_state`; `Same_idempotency_key_returns_same_response_and_performs_once` (two captures, one `capture_id`); `Lookup_returns_recorded_operation_or_404`; `Missing_api_key_returns_401`.
**Prove:** `dotnet test --project tests/Ledgerline.BankSimulator.Tests --filter-class "*BankOperationsTests"`
**Commit:** `feat(banksim): add idempotent authorize, capture, void and refund`

### M2-T07: Bank Simulator: faults and settlement file

- [x] Done

**Spec:** `bank-simulator.md` section 3 (faults, settlement, tampering), section 5. **Depends:** M2-T06.
**Files:** Create `Features/{FaultsEndpoint.cs,FaultState.cs,FaultMiddleware.cs,SettlementEndpoint.cs,SettlementTampering.cs}`. Test `FaultTests.cs`, `SettlementTests.cs`.
**Tests:** `Error500_fault_fails_next_n_requests_without_performing`; `SuccessThenTimeout_performs_operation_and_lookup_finds_it` (client timeout 500 ms, fault `delayMs` 2000); `Timeout_fault_does_not_perform_operation` (lookup 404); `Fault_expires_after_count`; `Settlement_lists_captures_and_refunds_of_the_day_only`; `Settlement_csv_has_header_and_spec_columns`; `Tampering_drops_or_alters_rows_in_file_only`.
**Prove:** `dotnet test --project tests/Ledgerline.BankSimulator.Tests`
**Commit:** `feat(banksim): add fault injection and settlement file`

### M2-T08: Saga core: top-up with bank steps and compensation (pure)

- [x] Done

**Spec:** `payments.md` section 4.1 (T1 to T10), 4.6. **Depends:** M1-T20, M2-T04.
**Files:** Modify `Sagas/PaymentSaga.cs`, `Sagas/SagaInput.cs`; create `src/Contracts/Ledgerline.Contracts/Payments/CallBank.cs` plus sample. Test `tests/Ledgerline.Payments.Tests/TopUpSagaTests.cs`.
**Tests:** one test per row T1 to T10, named `T01_start_sends_EvaluateRisk` through `T10_compensation_done_ends_Compensated_and_publishes_PaymentFailed`; plus `Risk_timeout_after_max_attempts_fails_with_RiskUnavailable`; `Bank_compensation_timeout_uses_exponential_backoff_capped_at_300s` (attempts 1 to 8 give 2, 4, 8, 16, 32, 64, 128, 256 s; cap applies from attempt 9 on if the limit is raised); `Bank_compensation_after_8_attempts_ends_ManualReview`; `Card_token_is_cleared_after_authorization` (decision carries a `ClearCardToken` effect); `M1_behavior_unchanged_when_risk_and_bank_disabled` (the M1 tests still pass).
**Prove:** `dotnet test --project tests/Ledgerline.Payments.Tests`
**Commit:** `feat(payments): extend top-up saga with risk, bank steps and compensation`

### M2-T09: Saga core: merchant payment, capture, void (pure)

- [x] Done

**Spec:** `payments.md` sections 4.2 (with risk), 4.3. **Depends:** M2-T08.
**Files:** Modify `Sagas/PaymentSaga.cs`. Test `tests/Ledgerline.Payments.Tests/{MerchantPaymentSagaTests.cs,TransferSagaTests.cs}`.
**Tests:** one test per row M1 to M12 and X1 to X5 with the same naming pattern; plus `Capture_request_when_not_Authorized_is_rejected_as_invalid_state` (decision flag `InvalidState = true`); `Capture_amount_above_authorized_is_invalid`; `Expiry_in_final_state_is_ignored`; `Automatic_capture_mode_goes_straight_to_LedgerCapture`.
**Prove:** `dotnet test --project tests/Ledgerline.Payments.Tests`
**Commit:** `feat(payments): add merchant payment saga`

### M2-T10: Accounts: holds (reserve, capture, release)

- [ ] Done

**Spec:** `accounts.md` section 4 (three commands), section 6 hold rules, invariant A7. **Depends:** M1-T16.
**Files:** Create contracts `ReserveFunds.cs`, `CaptureFunds.cs`, `ReleaseFunds.cs` plus samples; in Accounts `Features/LedgerCommands/{ReserveFundsHandler.cs,CaptureFundsHandler.cs,ReleaseFundsHandler.cs}`, `Ledger/Holds.cs`; modify `Ledger/Ledger.cs` (hold projection updates inside `PostAsync`), `LedgerAssert.cs` (new check: `PAYMENTS_PENDING` balance equals the sum of `Active` holds per currency), `RebuildProjectionsCommand.cs` (rebuild `holds`), `AccountResponse` (`heldMinor`). Test `tests/Ledgerline.Accounts.IntegrationTests/HoldTests.cs`.
**Tests:** `Reserve_moves_funds_to_pending_and_creates_active_hold`; `Reserve_with_insufficient_funds_is_rejected`; `Second_reserve_for_same_payment_is_rejected_HoldNotActive`; `Full_capture_pays_merchant_and_marks_hold_Captured`; `Partial_capture_returns_remainder_to_customer`; `Capture_above_hold_is_rejected_AmountExceedsHold`; `Capture_without_hold_is_rejected_HoldNotFound`; `Release_returns_funds_and_marks_hold_Released`; `Release_succeeds_when_customer_account_is_frozen`; `Capture_after_release_is_rejected_HoldNotActive`; `Duplicate_reserve_then_capture_commands_keep_hold_consistent` (exact name); `Concurrent_capture_and_release_have_one_winner` (50 holds, both commands at once each).
**Prove:** `dotnet test --project tests/Ledgerline.Accounts.IntegrationTests --filter-class "*HoldTests"`
**Commit:** `feat(accounts): add holds with reserve, capture and release`

### M2-T11: Accounts: refund command, freeze and unfreeze, trial balance

- [ ] Done

**Spec:** `accounts.md` section 3 (freeze, unfreeze, trial balance), section 4 (`refund-funds`, `account-status-changed`). **Depends:** M2-T10, M2-T01.
**Files:** Create contracts `RefundFunds.cs`, `AccountStatusChanged.cs` plus samples; in Accounts `Features/LedgerCommands/RefundFundsHandler.cs`, `Features/Accounts/{FreezeEndpoint.cs,UnfreezeEndpoint.cs}`, `Features/Ledger/TrialBalanceEndpoint.cs`. Test `tests/Ledgerline.Accounts.IntegrationTests/{RefundTests.cs,AdminApiTests.cs}`.
**Tests:** `Refund_moves_money_from_merchant_to_customer`; `Refund_with_insufficient_merchant_balance_is_rejected`; `Freeze_requires_admin`; `Freeze_appends_status_event_and_publishes_AccountStatusChanged`; `Freeze_twice_is_a_no_op_204`; `System_account_cannot_be_frozen_409`; `Unfreeze_allows_postings_again`; `Trial_balance_is_zero_after_mixed_postings`; `Trial_balance_requires_admin`.
**Prove:** `dotnet test --project tests/Ledgerline.Accounts.IntegrationTests`
**Commit:** `feat(accounts): add refunds, freeze and trial balance`

### M2-T12: Payments: bank client and call-bank handler

- [ ] Done

**Spec:** `payments.md` section 4.5, invariant P8. **Depends:** M2-T07, M2-T08.
**Files:** Create in Payments `Bank/{IBankClient.cs,BankClient.cs,BankResult.cs,BankOptions.cs}`, `Features/Bank/CallBankHandler.cs`; add `Microsoft.Extensions.Http.Resilience` 10.11.0 (timeout only, no retry for POST); modify `Program.cs`. Add a project reference from `Ledgerline.Payments.IntegrationTests` to `Ledgerline.BankSimulator` and host it with `ServiceHost<Ledgerline.BankSimulator.Program>`. Test `tests/Ledgerline.Payments.IntegrationTests/BankCallTests.cs`.
**Produces:** `IBankClient.AuthorizeAsync(Guid paymentId, Money amount, string cardToken, CancellationToken)`, `.CaptureAsync(Guid paymentId, string authorizationId, ...)`, `.VoidAsync(...)`, `.RefundAsync(Guid paymentId, string captureId, ...)`, `.LookupAsync(string idempotencyKey, ...)`, each returning `BankResult` (`Succeeded(id)`, `Declined(code)`, `Unknown`); crash point `bank.after-response` hit after a definitive answer and before the transition.
**Tests:** `Authorize_and_capture_drive_saga_to_LedgerCredit`; `Declined_authorization_fails_payment`; `Bank_timeout_with_success_is_resolved_by_lookup` (exact name; fault `SuccessThenTimeout`); `Bank_timeout_without_effect_is_retried_and_succeeds` (fault `Timeout`, count 1); `Bank_error_500_is_retried`; `Crash_after_bank_capture_before_commit_does_not_capture_twice` (exact name; Bank Simulator has exactly one capture operation); `Stale_call_bank_command_for_other_step_is_ignored`.
**Prove:** `dotnet test --project tests/Ledgerline.Payments.IntegrationTests --filter-class "*BankCallTests"`
**Commit:** `feat(payments): call the bank idempotently with lookup on unknown outcome`

### M2-T13: Payments: risk step wiring and full top-up flow

- [ ] Done

**Spec:** `payments.md` sections 4.1, 5; `design.md` flows 10.1 and 10.5. **Depends:** M2-T12, M2-T04.
**Files:** Create in Payments `Features/RiskReplies/{RiskApprovedHandler.cs,RiskRejectedHandler.cs}`, `Features/AccountReplica/AccountStatusChangedHandler.cs`; modify `Program.cs` (`RiskEnabled = true`, `BankEnabled = true` from configuration, default true), `CreateTopUpEndpoint.cs` (`cardToken`). Test `tests/Ledgerline.Payments.IntegrationTests/TopUpFlowTests.cs` (probe plays Risk and Accounts; Bank Simulator is real).
**Tests:** `TopUp_happy_path_completes_and_clears_card_token`; `Risk_rejection_ends_Rejected_without_bank_call` (Bank Simulator has zero operations); `Ledger_rejection_after_bank_capture_refunds_at_bank` (exact name; ends `Compensated`, bank has capture and refund); `Capture_declined_voids_authorization_and_ends_Compensated` (`tok_capture_fails`); `Exhausted_compensation_ends_in_manual_review` (exact name; fault `Error500` on `Refund` with count 100, backoff shortened); `Frozen_account_is_rejected_synchronously_when_replica_knows` (409).
**Prove:** `dotnet test --project tests/Ledgerline.Payments.IntegrationTests --filter-class "*TopUpFlowTests"`
**Commit:** `feat(payments): complete top-up saga with risk, bank and compensation`

### M2-T14: Payments: merchant payment endpoints, capture, void, expiry

- [ ] Done

**Spec:** `payments.md` sections 3, 4.3, 4.6 (expiry), invariants P1, P2. **Depends:** M2-T09, M2-T10, M2-T13.
**Files:** Create contracts `PaymentAuthorized.cs`, `PaymentCaptured.cs`, `PaymentVoided.cs` plus samples; in Payments `Features/Payments/{CreatePaymentEndpoint.cs,CapturePaymentEndpoint.cs,VoidPaymentEndpoint.cs,ListPaymentsEndpoint.cs}`. Test `tests/Ledgerline.Payments.IntegrationTests/MerchantPaymentFlowTests.cs`.
**Tests:** `Manual_payment_reaches_Authorized_with_expiry`; `Capture_by_merchant_reaches_Captured_with_amount`; `Partial_capture_sets_captured_minor`; `Capture_by_non_owner_returns_404`; `Capture_twice_returns_409_second_time`; `Capture_and_void_race_has_exactly_one_winner` (exact name); `Void_reaches_Voided`; `Authorization_expiry_releases_hold` (exact name; TTL shortened to 500 ms); `Automatic_capture_completes_without_merchant_call`; `Rejected_capture_returns_payment_to_Authorized_with_reason`; `List_payments_filters_by_account_and_status`.
**Prove:** `dotnet test --project tests/Ledgerline.Payments.IntegrationTests --filter-class "*MerchantPaymentFlowTests"`
**Commit:** `feat(payments): add merchant payments with capture, void and expiry`

### M2-T15: Payments: refunds

- [ ] Done

**Spec:** `payments.md` sections 3, 4.4. **Depends:** M2-T14, M2-T11.
**Files:** Create contract `PaymentRefunded.cs` plus sample; in Payments `Features/Refunds/{CreateRefundEndpoint.cs,ListRefundsEndpoint.cs,RefundSaga.cs,RefundResponse.cs}`; modify the ledger reply handlers to route by `operationId` to a refund when no payment step matches. Test `tests/Ledgerline.Payments.Tests/RefundSagaTests.cs`, `tests/Ledgerline.Payments.IntegrationTests/RefundFlowTests.cs`.
**Tests:** unit: rows R1 to R3. Integration: `Partial_refund_completes_and_updates_refunded_minor`; `Refunds_cannot_exceed_captured_amount_including_pending` (two concurrent refunds of 60% each: one `202`, one `409`); `Rejected_refund_is_Failed_and_frees_refundable_amount`; `Refund_of_uncaptured_payment_returns_409`; `Refund_is_idempotent_by_key`.
**Prove:** `dotnet test --project tests/Ledgerline.Payments.IntegrationTests --filter-class "*RefundFlowTests"` and `dotnet test --project tests/Ledgerline.Payments.Tests --filter-class "*RefundSagaTests"`
**Commit:** `feat(payments): add refunds`

### M2-T16: Notifications: recipients, handlers, sender, API

- [ ] Done

**Spec:** `notifications.md` (all, except the Phase 2 row). **Depends:** M2-T14, M2-T15.
**Files:** In Notifications: `Infrastructure/{NotificationsDbContext.cs,Entities/*.cs,Migrations/Initial,MigrateCommand.cs}`, `Features/Recipients/AccountOpenedHandler.cs`, `Features/PaymentEvents/*.cs` (one handler per consumed type, sharing `NotificationPlanner`), `Templates/{TemplateRenderer.cs,Templates.cs}`, `Sending/{NotificationSender.cs,IEmailSender.cs,SmtpEmailSender.cs}`, `Features/List/ListNotificationsEndpoint.cs`; modify `Program.cs`. SMTP: Microsoft's documentation recommends against `System.Net.Mail.SmtpClient` for new code. Use MailKit: first check its current version and license on nuget.org and add the row to ADR 0000. Create `tests/Ledgerline.Notifications.IntegrationTests/` project. Tests `tests/Ledgerline.Notifications.Tests/TemplateTests.cs`, `tests/Ledgerline.Notifications.IntegrationTests/NotificationFlowTests.cs` (fake `IEmailSender` recording sends; one test uses a Mailpit container and its HTTP API).
**Tests:** unit: `Amount_is_rendered_as_major_units_with_currency` (`150000 TRY` gives `1,500.00 TRY`, invariant culture); one rendering test per template. Integration: `PaymentCompleted_creates_one_notification_for_customer`; `Transfer_completed_notifies_sender_and_receiver`; `Same_event_twice_creates_one_notification`; `Unknown_recipient_is_retried_then_stored_as_Skipped`; `Smtp_failure_retries_with_backoff_then_Failed_after_6_attempts`; `Sender_failure_does_not_block_consumer` (events keep being consumed while SMTP is down); `Email_address_never_appears_in_logs` (capture logs through a test logger provider, assert no `@example.test`); `Email_reaches_Mailpit` (real SMTP); `List_returns_only_own_notifications`.
**Prove:** `dotnet test --project tests/Ledgerline.Notifications.IntegrationTests` and `dotnet test --project tests/Ledgerline.Notifications.Tests`
**Commit:** `feat(notifications): send payment emails`

### M2-T17: Timeouts complete: attempt accounting during outages

- [ ] Done

**Spec:** `payments.md` sections 4.6 and 9 (last paragraph). **Depends:** M2-T13.
**Files:** Modify `Sagas/SagaTimeoutPoller.cs`, `Sagas/SagaRunner.cs` (store the `message_id` of the last command sent for the step on the payment or timeout row). Test `tests/Ledgerline.Payments.IntegrationTests/TimeoutTests.cs` (extend).
**Tests:** `Step_attempts_are_not_consumed_while_broker_is_down` (stop RabbitMQ for 10 timeouts' worth of time with shortened options, start it, payment completes, `step_attempt <= 2`); `Topups_accepted_during_broker_outage_complete_after_recovery` (exact name); `Risk_unavailable_fails_payment_after_5_attempts_with_RiskUnavailable`.
**Implement:** before treating a due timeout as an attempt, check that the outbox row of the last command for that step has `processed_at` set. If not, push `due_at` forward by the step timeout and do nothing else.
**Prove:** `dotnet test --project tests/Ledgerline.Payments.IntegrationTests --filter-class "*TimeoutTests"`
**Commit:** `fix(payments): do not consume step attempts during broker outage`

### M2-T18: Gateway

- [ ] Done

**Spec:** `gateway.md`. **Depends:** M2-T01.
**Files:** In Gateway: `Program.cs`, `appsettings.json` (routes and clusters of the spec, Phase 2 routes left out), `CorrelationIdMiddleware.cs`, `RateLimiting.cs`. Add `Yarp.ReverseProxy` 2.3.0. Reference `Ledgerline.Testing` from `Ledgerline.Gateway.Tests`. Test `tests/Ledgerline.Gateway.Tests/GatewayTests.cs` (stub upstreams: in-test Kestrel servers that echo method, path, and headers).
**Tests:** `Routes_map_public_paths_to_upstream_paths` (theory over the route table); `Unauthenticated_request_is_rejected_with_401_before_reaching_upstream`; `Admin_route_denies_customer_with_403`; `Correlation_id_is_generated_forwarded_and_returned`; `Valid_incoming_correlation_id_is_preserved`; `Authorization_and_idempotency_headers_are_forwarded`; `Rate_limit_returns_429_with_retry_after` (limit lowered through configuration); `Body_larger_than_64kb_returns_413`; `Upstream_down_returns_503_for_that_route_only`; `Health_endpoints_are_not_proxied`; `Post_is_not_retried_on_upstream_failure` (stub counts one call).
**Prove:** `dotnet test --project tests/Ledgerline.Gateway.Tests`
**Commit:** `feat(gateway): add YARP gateway with auth, rate limiting and correlation`

### M2-T19: Compose for the whole system, architecture rules, README

- [ ] Done

**Spec:** ADR 0005; `design.md` section 5. **Depends:** M2-T16, M2-T17, M2-T18.
**Files:** Create Dockerfiles for Risk, Notifications, BankSimulator, Gateway; modify `compose.yaml` (profile `app`: all services with migrate jobs, gateway on host port 8080, service host ports removed except the gateway; `deploy/compose/postgres/init-databases.sql` unchanged); modify `scripts/smoke.*` (through the gateway with Keycloak tokens: open wallets for `alice`, `bob`, `shop`; top-up with `tok_ok`; transfer; merchant payment with manual capture; partial refund; assert balances and that Mailpit received at least 4 emails; assert trial balance zero as `admin`). Modify `tests/Ledgerline.ArchitectureTests/ServiceIsolationTests.cs`: add `Consumers_are_registered_only_through_MessagingBuilder` (no type outside `Ledgerline.BuildingBlocks.Messaging` references `RabbitMQ.Client`), `Only_ledger_namespace_writes_account_balances` (the `BalanceMinor` setter of the account entity is `private`; the only mutating method is `ApplyPosting(Direction, long)`; the test asserts by reflection that the setter is private and that no public or internal method other than `ApplyPosting` exists on the entity that takes an amount), `Amounts_are_never_floating_point` (no property or parameter named `*Amount*` or `*Minor*` of type `decimal`, `double`, `float` in any Ledgerline assembly). Modify `README.md` (M2 status, full architecture diagram, flows link).
**Prove:** `docker compose --profile app up --build -d --wait && ./scripts/smoke.sh` prints `SMOKE OK` and exits 0; `dotnet test --solution Ledgerline.slnx` passes.
**Commit:** `feat: run the full system through the gateway with one command`

## Milestone 2 checklist

- [ ] Saga with compensation works and is tested: `TopUpFlowTests`, `BankCallTests` (M2-T12, M2-T13).
- [ ] Risk and Notifications run as separate services (M2-T04, M2-T05, M2-T16).
- [ ] Gateway is the only public entry (M2-T18, M2-T19).
- [ ] Real authentication through Keycloak; no dev header path remains (M2-T01).
- [ ] One command still starts everything (M2-T19). **DoD: one command**
- [ ] Architecture diagram in README matches the running system (M2-T19). **DoD: architecture diagram**
- [ ] `payment captures at most once` proven: `Capture_and_void_race_has_exactly_one_winner`, `HoldTests` (M2-T10, M2-T14).
- [ ] CI green on the last commit.

---

# Milestone 3: failure-scenario tests, k6, OpenTelemetry dashboards, CI security scans

### M3-T01: OpenTelemetry wiring and one trace across the broker

- [ ] Done

**Spec:** ADR 0009; `building-blocks.md` section 9 (Telemetry). **Depends:** M2 complete.
**Files:** Create in Web `Telemetry/TelemetryExtensions.cs`; in Messaging `Diagnostics/MessagingActivitySource.cs` (source name `Ledgerline.Messaging`), modify `OutboxRelay.cs`, `RabbitMqOutboxPublisher.cs`, `ConsumerHost.cs`. Add `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http` 1.21.0 (check the exact latest version of each instrumentation package on nuget.org; they version independently), and an Npgsql OpenTelemetry package matching the Npgsql version. Modify every service `Program.cs` (`builder.AddLedgerlineTelemetry("<service>")`), `compose.yaml` (`OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-lgtm:4317`). Test `tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests/TracingTests.cs` (in-memory exporter from `OpenTelemetry.Exporter.InMemory`).
**Tests:** `Consumer_span_is_child_of_the_span_that_enqueued_the_message` (same `TraceId`, parent chain: enqueue scope, producer span, consumer span); `Producer_and_consumer_spans_carry_messaging_attributes` (`messaging.system`, `messaging.destination.name`, `messaging.message.id`); `Retry_delivery_stays_in_the_same_trace`; `Trace_spans_two_services_in_round_trip`.
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests --filter-class "*TracingTests"`; then start the stack, run `./scripts/smoke.sh`, open Grafana `http://localhost:3000` Explore, Tempo, and confirm one top-up trace contains spans from gateway, payments, risk, accounts, notifications.
**Commit:** `feat(observability): trace requests across the outbox and broker`

### M3-T02: Custom metrics

- [ ] Done

**Spec:** ADR 0009 metric table. **Depends:** M3-T01.
**Files:** Create in Messaging `Diagnostics/MessagingMetrics.cs`; in Payments `Sagas/SagaMetrics.cs`; in Accounts `Ledger/LedgerMetrics.cs` (the imbalance gauge reads a cached trial balance refreshed every 15 s). Test `tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests/MetricsTests.cs`, `tests/Ledgerline.Payments.IntegrationTests/MetricsTests.cs`, `tests/Ledgerline.Accounts.IntegrationTests/MetricsTests.cs` (use `MeterListener`).
**Tests:** `Outbox_lag_is_recorded_per_published_message`; `Outbox_pending_gauge_rises_while_broker_is_down`; `Inbox_duplicate_increments_counter`; `Dead_lettered_message_increments_counter`; `Saga_duration_is_recorded_with_kind_and_outcome_tags`; `Manual_review_increments_counter`; `Ledger_imbalance_gauge_is_zero_after_postings`.
**Prove:** `dotnet test --solution Ledgerline.slnx --filter-class "*MetricsTests"`
**Commit:** `feat(observability): add outbox, inbox, saga and ledger metrics`

### M3-T03: Grafana dashboards as code

- [ ] Done

**Spec:** ADR 0009. **Depends:** M3-T02.
**Files:** Create `deploy/observability/dashboards/ledgerline-overview.json` (panels: request rate and p95 per service, saga duration p50/p95 by kind, saga outcomes, outbox pending and lag p95 per service, inbox duplicates rate, consumer retries and dead letters, ledger posting duration p95, ledger imbalance stat, manual review stat), `deploy/observability/provisioning/dashboards.yaml`; modify `compose.yaml` (mount both into `otel-lgtm`; check the image documentation for the provisioning paths, they are not verified here). Create `docs/observability.md` (how to open the dashboard and find a trace by payment ID).
**Tests:** `scripts/check-dashboard.sh`: starts the stack, runs the smoke script, queries `http://localhost:3000/api/search?query=Ledgerline` and expects one dashboard, queries Prometheus through Grafana for `ledgerline_outbox_lag_seconds_count > 0`.
**Prove:** `./scripts/check-dashboard.sh` exits 0.
**Commit:** `feat(observability): provision Grafana dashboard`

### M3-T04: Remaining integration failure tests

- [ ] Done

**Spec:** `testing.md` sections F1 to F5. **Depends:** M2 complete.
**Files:** Create `tests/Ledgerline.ArchitectureTests/FailureTestInventoryTests.cs` with the list of every exact test name from `testing.md` F1 to F5 (integration rows) and one test `Every_documented_failure_test_exists` that reflects over the integration test assemblies (add project references) and fails for a missing name. Then add any test the inventory reports missing in its owning project.
**Tests:** `Every_documented_failure_test_exists`.
**Prove:** `dotnet test --project tests/Ledgerline.ArchitectureTests --filter-method "*Every_documented_failure_test_exists"`
**Commit:** `test: enforce the documented failure test inventory`

### M3-T05: System test harness

- [ ] Done

**Spec:** `testing.md` section 3 (System rows), section 4. **Depends:** M2-T19.
**Files:** In `tests/Ledgerline.SystemTests/`: `Harness/ComposeStack.cs` (`UpAsync`, `StopAsync(service)`, `StartAsync(service)`, `KillAsync(service)` using `docker compose -p ledgerline-systest --profile app ...` through `System.Diagnostics.Process`; collects logs on failure), `Harness/KeycloakTokens.cs`, `Harness/LedgerlineClient.cs` (typed calls through the gateway), `Harness/Workloads.cs`, `Harness/SystemInvariants.cs` (trial balance zero through the admin API; sum of wallet balances equals completed top-ups minus nothing else; every payment final), `SmokeSystemTests.cs`. Tests are skipped unless environment variable `LEDGERLINE_SYSTEM_TESTS=1` (so `dotnet test --solution` stays fast).
**Tests:** `Stack_starts_and_smoke_flow_passes`.
**Prove:** `LEDGERLINE_SYSTEM_TESTS=1 dotnet test --project tests/Ledgerline.SystemTests --filter-class "*SmokeSystemTests"`
**Commit:** `test(system): add Compose-based system test harness`

### M3-T06: System failure tests: broker outage and service kills

- [ ] Done

**Spec:** `testing.md` F1 and F2 system rows. **Depends:** M3-T05.
**Files:** `tests/Ledgerline.SystemTests/FailureSystemTests.cs`.
**Tests (exact names):** `Broker_outage_loses_no_money`; `Killing_payments_mid_saga_completes_after_restart`; `Killing_accounts_mid_posting_completes_after_restart`. Each ends with `SystemInvariants.AssertAsync` and, for the payments kill test, compares Bank Simulator's settlement file of today with ledger `TopUp` entries.
**Prove:** `LEDGERLINE_SYSTEM_TESTS=1 dotnet test --project tests/Ledgerline.SystemTests --filter-class "*FailureSystemTests"` passes 5 times in a row (`for i in 1 2 3 4 5; do ... || break; done`).
**Commit:** `test(system): prove broker outage and service crash lose no money`

### M3-T07: System failure test: replay every message

- [ ] Done

**Spec:** `testing.md` F3 system row. **Depends:** M3-T05.
**Files:** `tests/Ledgerline.SystemTests/Harness/ReplayShovel.cs` (binds an exclusive queue with `#` to the three exchanges, re-publishes every message once with the same `messageId` and once with a new `messageId`), `ReplaySystemTests.cs`. Expose RabbitMQ port in the system test Compose project only.
**Tests:** `Replaying_every_message_changes_nothing` (exact name).
**Prove:** `LEDGERLINE_SYSTEM_TESTS=1 dotnet test --project tests/Ledgerline.SystemTests --filter-class "*ReplaySystemTests"`
**Commit:** `test(system): prove duplicate delivery of every message is harmless`

### M3-T08: k6 library, smoke and steady scenarios

- [ ] Done

**Spec:** `testing.md` section 5. **Depends:** M2-T19.
**Files:** `tests/k6/lib/{auth.js,api.js,setup.js,saga.js,thresholds.js}`, `tests/k6/{smoke.js,topup-steady.js,transfer-steady.js}`, `scripts/k6.sh` and `scripts/k6.ps1` (wrap the `docker run grafana/k6:2.4.0` command, write `--summary-export` to `docs/results/raw/<scenario>-<date>.json`). First confirm the k6 2.x script API for `http`, `scenarios`, `thresholds`, and custom `Trend` against the k6 documentation; adjust if 2.x changed them.
**Tests:** thresholds inside the scripts are the tests (values from the target table in `testing.md`).
**Prove:** `./scripts/k6.sh smoke` exits 0 with all thresholds passed.
**Commit:** `test(load): add k6 smoke and steady scenarios`

### M3-T09: k6 contention, mix, spike, outage scenarios and the monolith baseline

- [ ] Done

**Spec:** `testing.md` section 5; `when-monolith.md` (baseline). **Depends:** M3-T08.
**Files:** `tests/k6/{transfer-hot-account.js,payment-mix.js,spike.js,broker-outage.js}`; `tests/Ledgerline.Accounts.IntegrationTests/Benchmarks/LocalTransferBaseline.cs` (a test, skipped unless `LEDGERLINE_BENCH=1`, that runs 10 000 transfers by calling `ILedger.PostAsync` directly in a transaction with 16 parallel workers and prints p50, p95, and transfers per second).
**Prove:** each of `./scripts/k6.sh transfer-hot-account`, `payment-mix`, `spike` runs to completion (thresholds may fail; record that); `LEDGERLINE_BENCH=1 dotnet test --project tests/Ledgerline.Accounts.IntegrationTests --filter-class "*LocalTransferBaseline"` prints the three numbers.
**Commit:** `test(load): add contention, mix and spike scenarios and a local baseline`

### M3-T10: Results write-up

- [ ] Done

**Depends:** M3-T03, M3-T09.
**Files:** Create `docs/results/k6-<yyyy-mm-dd>.md` (hardware, Docker limits, commit hash, one table per scenario: target, measured, pass or miss, and a short explanation for each miss), `docs/results/img/{dashboard.png,trace-topup.png}` (screenshots taken by the owner or by browser automation from Grafana); modify `README.md` ("Measurements" section: summary table, both images), `docs/when-monolith.md` (fill "to be measured" cells from the baseline and `transfer-steady`). If the hot-account scenario misses its target, add a section to ADR 0007 with the numbers and the decision (shard or accept), do not silently tune the target.
**Prove:** `grep -c "to be measured" docs/when-monolith.md` prints 0; `grep -c "TBD" README.md` prints 0 for the Measurements section; both image files exist.
**Commit:** `docs: publish k6 results, dashboard and trace screenshots`

### M3-T11: CI security scans

- [ ] Done

**Spec:** `testing.md` section 4; ADR 0000 (action versions; verify `github/codeql-action` major). **Depends:** none.
**Files:** Modify `.github/workflows/ci.yml` (job `security`: `actions/dependency-review-action@v5` on pull requests; step `dotnet list Ledgerline.slnx package --vulnerable --include-transitive` failing when the output contains `has the following vulnerable packages`; `gitleaks/gitleaks-action@v3`); create `.github/workflows/codeql.yml` (C#, manual build mode with `dotnet build`), `.github/dependabot.yml` (nuget, github-actions, docker; weekly), `.gitleaks.toml` (allowlist the documented development passwords in `compose.yaml`, `deploy/compose/**`, and the realm file by path and exact value; nothing broader).
**Tests:** run gitleaks locally: `docker run --rm -v "$PWD:/repo" zricethezav/gitleaks:latest detect --source /repo --config /repo/.gitleaks.toml` (check the current image name and tag first).
**Prove:** the local gitleaks run reports no leaks; `dotnet list Ledgerline.slnx package --vulnerable --include-transitive` reports none; after the owner pushes, the `security` and `codeql` workflows are green.
**Commit:** `ci: add dependency, secret and static analysis scans`

### M3-T12: CI image scan, system tests, k6 smoke; remove the zero-test exemption

- [ ] Done

**Spec:** `testing.md` section 4. **Depends:** M3-T06, M3-T08, M3-T11.
**Files:** Modify `.github/workflows/ci.yml`: job `images` (main only: `docker compose --profile app build`, `aquasecurity/trivy-action@v0.36.0` per image, `severity: HIGH,CRITICAL`, `ignore-unfixed: true`, `exit-code: 1`); job `system-tests` (main and `schedule` nightly: `LEDGERLINE_SYSTEM_TESTS=1`, upload `docker compose logs` as an artifact on failure); job `k6-smoke` (nightly). Modify `tests/Directory.Build.props`: remove the `--ignore-exit-code 8` line; delete or fill any test project that still has zero tests.
**Prove:** `dotnet test --solution Ledgerline.slnx` exits 0 without the exemption; `docker run --rm -v /var/run/docker.sock:/var/run/docker.sock aquasec/trivy:latest image --severity HIGH,CRITICAL --ignore-unfixed --exit-code 1 ledgerline-accounts` exits 0 (check the image name Compose produced with `docker compose --profile app images`).
**Commit:** `ci: scan images, run system tests and k6 smoke`

### M3-T13: README for M3

- [ ] Done

**Depends:** M3-T10, M3-T12.
**Files:** Modify `README.md`: "Failure scenarios" section with a table (scenario, test name, level, link to the test file), CI badges, "Security scans" section listing the four scans and what each covers.
**Prove:** every test name in the README table exists: `for n in $(grep -o '`[A-Z][A-Za-z_]*`' README.md | tr -d '`' | grep _); do grep -rq "$n" tests || echo MISSING $n; done` prints nothing.
**Commit:** `docs: document failure scenarios, measurements and scans`

## Milestone 3 checklist

- [ ] Broker down: integration and system tests pass (M1-T11, M2-T17, M3-T06). **DoD: failure test 1**
- [ ] Service crashes: integration and system tests pass (M1-T23, M2-T12, M3-T06). **DoD: failure test 2**
- [ ] Message delivered twice: integration and system tests pass (M1-T08, M1-T16, M3-T07). **DoD: failure test 3**
- [ ] Documented failure test inventory is enforced (M3-T04).
- [ ] OpenTelemetry traces across services and a dashboard screenshot in the README (M3-T01, M3-T03, M3-T10). **DoD: traces and dashboard screenshot**
- [ ] k6 results in the README with hardware and misses stated (M3-T10). **DoD: k6 results**
- [ ] Tests and four security scans run in CI (M3-T11, M3-T12). **DoD: tests and security scans in CI**
- [ ] `when-monolith.md` has measured numbers (M3-T10). **DoD: when monolith section**

---

# Milestone 4: Kubernetes with Helm and health checks

### M4-T01: Tooling and kind cluster definition

- [ ] Done

**Spec:** ADR 0011, ADR 0000. **Depends:** M3 complete.
**Files:** Create `deploy/kind/cluster.yaml` (one control-plane node, `extraPortMappings` container port 30080 to host 8080, 30300 to host 3000, 30025 to host 8025), `scripts/kind-up.sh` and `.ps1` (create cluster `ledgerline` if absent), `scripts/kind-down.sh` and `.ps1`, `docs/kubernetes.md` (install instructions for kind v0.33.0 and Helm v4.3.2 on Windows, macOS, Linux; re-verify both versions first).
**Prove:** `kind version && helm version --short && ./scripts/kind-up.sh && kubectl --context kind-ledgerline get nodes` shows one `Ready` node.
**Commit:** `chore(k8s): add kind cluster definition and scripts`

### M4-T02: Health and shutdown behavior required by Kubernetes

- [ ] Done

**Spec:** `building-blocks.md` sections 8 (graceful shutdown) and 9 (Health); ADR 0011. **Depends:** M3 complete.
**Files:** Modify Web `Health/HealthEndpoints.cs` (ready check includes a RabbitMQ connection check supplied by Messaging through `IHealthCheck` registration), Messaging `RabbitMq/RabbitMqHealthCheck.cs`, Web `WebDefaults.cs` (`HostOptions.ShutdownTimeout = 25 s`; readiness returns 503 as soon as `ApplicationStopping` fires). Test `tests/Ledgerline.Accounts.IntegrationTests/HealthTests.cs`.
**Tests:** `Ready_is_503_when_database_is_unreachable` (host configured with a wrong database port); `Ready_is_503_while_broker_is_down_and_recovers` (uses fixture stop and start); `Live_stays_200_while_dependencies_are_down`; `Ready_turns_503_when_shutdown_starts`; `In_flight_message_completes_during_shutdown`.
**Prove:** `dotnet test --project tests/Ledgerline.Accounts.IntegrationTests --filter-class "*HealthTests"`
**Commit:** `feat(web): make readiness and shutdown Kubernetes-friendly`

### M4-T03: Helm chart: generic service templates

- [ ] Done

**Spec:** ADR 0011. **Depends:** M4-T01.
**Files:** Create `deploy/helm/ledgerline/Chart.yaml`, `values.yaml` (map `services:` with entries `accounts`, `payments`, `risk`, `notifications`, `banksim`, `gateway`: image, replicas, port, env, `migrate: true|false`, `nodePort` for the gateway), `templates/_helpers.tpl`, `templates/service/{deployment.yaml,service.yaml,configmap.yaml,pdb.yaml}` (each a `range` over `.Values.services`), `templates/secrets.yaml`. Deployment: non-root security context, resource requests and limits, `startupProbe` (`/health/live`, 30 x 2 s), `livenessProbe` (`/health/live`), `readinessProbe` (`/health/ready`), `terminationGracePeriodSeconds: 30`, `preStop` sleep 5 s. Create `deploy/helm/ledgerline/tests/render_test.sh`.
**Tests:** `render_test.sh`: `helm lint`, `helm template`, then assert with `grep` or `yq` that every Deployment has the three probes, a non-root security context, and resource limits; that only the gateway Service is `NodePort`.
**Prove:** `./deploy/helm/ledgerline/tests/render_test.sh` prints `RENDER OK`.
**Commit:** `feat(k8s): add Helm chart with generic service templates`

### M4-T04: Helm chart: migration jobs

- [ ] Done

**Spec:** ADR 0011; `design.md` section 11. **Depends:** M4-T03.
**Files:** Create `templates/service/migrate-job.yaml` (hook `pre-install,pre-upgrade`, weight after infrastructure, `backoffLimit: 6`, `hook-delete-policy: before-hook-creation,hook-succeeded`, args `["migrate"]`, an init container that waits for PostgreSQL with `pg_isready`). Extend `render_test.sh`.
**Tests:** render test asserts one Job per service with `migrate: true` and none for the gateway.
**Prove:** `./deploy/helm/ledgerline/tests/render_test.sh`
**Commit:** `feat(k8s): run migrations as Helm hook jobs`

### M4-T05: Helm chart: infrastructure templates

- [ ] Done

**Spec:** ADR 0011. **Depends:** M4-T03.
**Files:** Create `templates/infra/{postgres.yaml,rabbitmq.yaml,redis.yaml,keycloak.yaml,otel-lgtm.yaml,mailpit.yaml}` (StatefulSet with a PVC for PostgreSQL and RabbitMQ; Deployments for the rest; ConfigMaps carrying `init-databases.sql`, the realm JSON, and the Grafana dashboard, loaded with `.Files.Get` from `deploy/helm/ledgerline/files/`, which are copies kept in sync by `scripts/sync-helm-files.sh` and checked by the render test), each guarded by `infra.<name>.enabled`. Infrastructure that must exist before migration hooks is itself installed as `pre-install` hooks with a lower weight, or the migrate Job's init container waits for it; choose the init-container wait (simpler, no hook ordering).
**Tests:** render test: with `--set infra.postgres.enabled=false` no PostgreSQL objects render; files in `files/` are byte-identical to their sources.
**Prove:** `./deploy/helm/ledgerline/tests/render_test.sh`
**Commit:** `feat(k8s): add dev-grade infrastructure templates`

### M4-T06: One command on Kubernetes and smoke

- [ ] Done

**Spec:** ADR 0011. **Depends:** M4-T02, M4-T04, M4-T05.
**Files:** Create `scripts/kind-deploy.sh` and `.ps1` (build images with `docker compose --profile app build`, `kind load docker-image` each, `helm upgrade --install ledgerline deploy/helm/ledgerline --wait --timeout 10m`), modify `scripts/smoke.*` to take the base URL and Keycloak URL as arguments. Keycloak issuer: tokens are requested from inside the cluster's point of view and validated by services with the in-cluster authority; set `KC_HOSTNAME` so that the issuer is the same string for the smoke script and the services (document the chosen approach in `docs/kubernetes.md`).
**Prove:** `./scripts/kind-up.sh && ./scripts/kind-deploy.sh && ./scripts/smoke.sh http://localhost:8080` prints `SMOKE OK`; `kubectl get pods` shows every pod `Ready`.
**Commit:** `feat(k8s): deploy the system to kind with one script`

### M4-T07: Resilience on Kubernetes

- [ ] Done

**Spec:** ADR 0011 (proof for the definition of done). **Depends:** M4-T06.
**Files:** Create `scripts/kind-resilience.sh`: start a 60 s workload (reuse `tests/k6/smoke.js` against the kind gateway), after 15 s run `kubectl delete pod -l app.kubernetes.io/name=payments --grace-period=0 --force`, after 30 s the same for `accounts`, wait for the workload, then assert through the API: no payment `Processing`, trial balance zero, zero `ManualReview`. Also `kubectl rollout restart deployment/payments` during a second run and assert zero failed requests at the gateway other than `503` during the restart window (replicas set to 2 for this check through `--set services.payments.replicas=2`).
**Prove:** `./scripts/kind-resilience.sh` prints `RESILIENCE OK`.
**Commit:** `test(k8s): prove pod deletion and rolling restart lose no money`

### M4-T08: CI job for kind, README

- [ ] Done

**Depends:** M4-T07.
**Files:** Modify `.github/workflows/ci.yml`: job `kind` on main (`helm/kind-action@v1.15.2` with `deploy/kind/cluster.yaml`, `azure/setup-helm@v5`, `scripts/kind-deploy.sh`, smoke, `scripts/kind-resilience.sh`, upload `kubectl logs` of all pods on failure); add `render_test.sh` to the `compose-validate` job. Modify `README.md` ("Run on Kubernetes" section with the three commands, the probe table, and the stated limits of the dev-grade infrastructure).
**Prove:** `./deploy/helm/ledgerline/tests/render_test.sh` passes locally; after push, the `kind` job is green.
**Commit:** `ci: deploy to kind and run smoke and resilience checks`

## Milestone 4 checklist

- [ ] Runs on Kubernetes through Helm (M4-T06). **DoD: runs on Kubernetes**
- [ ] Startup, liveness, readiness probes on every service, tested (M4-T02, M4-T03). **DoD: health checks**
- [ ] Pod deletion and rolling restart proven harmless (M4-T07).
- [ ] CI deploys to kind (M4-T08).

---

# Milestone 5: Phase 2 (Kafka, MongoDB read model, reconciliation)

Before starting: re-verify the Phase 2 versions in ADR 0000 (`apache/kafka`, `mongo`,
`Confluent.Kafka`, `MongoDB.Driver`, `Quartz`, `Testcontainers.Kafka`,
`Testcontainers.MongoDb`) and update the table.

### M5-T01: Kafka and MongoDB in Compose and the test fixture

- [ ] Done

**Spec:** `reporting.md` section 2. **Depends:** M4 complete.
**Files:** Modify `compose.yaml` (profile `phase2`: `kafka` in KRaft single-node mode with a healthcheck, `mongo`, a one-shot `kafka-init` creating the three topics with 6 partitions and `retention.ms=-1`); `tests/Ledgerline.Testing/InfrastructureFixture.cs` (lazy `KafkaBootstrapServers`, `MongoConnectionString` started on first use). Add `Testcontainers.Kafka`, `Testcontainers.MongoDb`, `Confluent.Kafka`, `MongoDB.Driver`. Test `tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests/Phase2FixtureTests.cs`.
**Tests:** `Kafka_accepts_produce_and_consume`; `Mongo_accepts_insert_and_find`.
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests --filter-class "*Phase2FixtureTests"` and `docker compose --profile phase2 up -d --wait kafka mongo`
**Commit:** `chore(phase2): add Kafka and MongoDB to Compose and fixtures`

### M5-T02: Outbox transport column and Kafka publisher

- [ ] Done

**Spec:** `reporting.md` section 2 (last paragraph); `building-blocks.md` section 6 (`transport`). **Depends:** M5-T01.
**Files:** Create `src/BuildingBlocks/Ledgerline.BuildingBlocks.Messaging.Kafka/` project (so services without Kafka do not load librdkafka): `KafkaOutboxPublisher.cs`, `KafkaTopicMap.cs`, `KafkaMessagingExtensions.cs`. Modify Messaging: `IOutboxPublisher` gains `string Transport { get; }`; `OutboxRelay` runs one loop per registered publisher filtering by `transport`; `IOutbox.Enqueue` consults `IOutboxRouting` to decide which transports a type goes to and writes one row per transport. Test `tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests/KafkaOutboxTests.cs`.
**Produces:** `m.AlsoPublishToKafka<TMessage>(string topic, Func<TMessage, string> key)` on `MessagingBuilder` (extension from the Kafka project); `m.AlsoPublishToKafkaExpanded<TMessage, TRecord>(topic, Func<TMessage, IEnumerable<TRecord>> expand, Func<TRecord, string> key)` for one-record-per-line.
**Tests:** `Event_routed_to_both_transports_writes_two_outbox_rows_with_different_message_ids`; `Kafka_row_is_produced_with_configured_key_and_envelope_value`; `Kafka_outage_does_not_delay_rabbit_delivery` (stop Kafka, RabbitMQ row delivered within 2 s, Kafka row stays pending and is delivered after start); `Producer_is_idempotent_with_acks_all` (assert the config); `Records_with_same_key_keep_order` (100 records, one key).
**Prove:** `dotnet test --project tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests --filter-class "*KafkaOutboxTests"`
**Commit:** `feat(messaging): relay selected events to Kafka through the outbox`

### M5-T03: Accounts and Payments publish to Kafka

- [ ] Done

**Spec:** `reporting.md` sections 2 and 5 (accounts topic). **Depends:** M5-T02.
**Files:** Modify Accounts `Program.cs` (`JournalEntryPosted` expanded per line to `ledgerline.ledger.entries.v1` keyed by `accountId`; `AccountOpened` to `ledgerline.accounts.events.v1`), Payments `Program.cs` (all `payments.event.*` to `ledgerline.payments.events.v1` keyed by `paymentId`); contracts: `JournalEntryLinePosted.cs` (`accounts.event.journal-entry-line-posted.v1`: entry fields plus one line and `lineNo`) with sample. Kafka is optional per configuration (`Messaging:Kafka:Enabled`, default false) so M1 to M4 setups keep working. Test `tests/Ledgerline.Accounts.IntegrationTests/KafkaPublishingTests.cs`, `tests/Ledgerline.Payments.IntegrationTests/KafkaPublishingTests.cs`.
**Tests:** `Posting_produces_one_kafka_record_per_line_keyed_by_account`; `Payment_events_are_produced_keyed_by_payment`; `With_kafka_disabled_no_kafka_rows_are_written`.
**Prove:** `dotnet test --solution Ledgerline.slnx --filter-class "*KafkaPublishingTests"`
**Commit:** `feat(phase2): publish ledger and payment events to Kafka`

### M5-T04: Reporting service: consumer loop and account statements

- [ ] Done

**Spec:** `reporting.md` sections 3, 4 (`account_statements`), 6 (Q1), 7. **Depends:** M5-T03.
**Files:** Create `src/Services/Reporting/Ledgerline.Reporting/` (web project, references `BuildingBlocks`, `BuildingBlocks.Web`, `Contracts`; not `BuildingBlocks.Messaging.Kafka`: it consumes with `Confluent.Kafka` directly): `Program.cs`, `Consuming/{KafkaConsumerLoop.cs,IProjection.cs,DeadLetterProducer.cs}`, `Projections/AccountStatementProjection.cs`, `Infrastructure/MongoCollections.cs`; `tests/Ledgerline.Reporting.IntegrationTests/` with `AccountStatementTests.cs`. Add both to the solution and the service to `ServiceIsolationTests.Deployables`.
**Produces:** `IProjection { IReadOnlyCollection<string> MessageTypes; Task ApplyAsync(MessageEnvelope envelope, CancellationToken ct); }`; offsets committed only after all projections applied the batch.
**Tests:** `Ledger_line_record_becomes_statement_document`; `Applying_same_record_twice_leaves_one_identical_document`; `Offsets_are_not_committed_when_mongo_write_fails` (fault-injected collection wrapper: record is re-consumed and applied after recovery); `Poison_record_goes_to_dead_topic_after_5_attempts`; `Index_on_account_and_posted_at_exists`.
**Prove:** `dotnet test --project tests/Ledgerline.Reporting.IntegrationTests --filter-class "*AccountStatementTests"`
**Commit:** `feat(reporting): project ledger lines into account statements`

### M5-T05: Reporting: payment timelines and merchant daily totals

- [ ] Done

**Spec:** `reporting.md` section 4 (two collections). **Depends:** M5-T04.
**Files:** `Projections/{PaymentTimelineProjection.cs,MerchantDailyTotalsProjection.cs}`. Test `ProjectionTests.cs`.
**Tests:** `Timeline_collects_events_once_each_and_tracks_latest_status`; `Out_of_order_older_event_does_not_overwrite_summary`; `Timeline_duplicate_event_is_not_added_twice`; `Daily_totals_increment_on_capture_and_refund`; `Daily_totals_duplicate_message_does_not_double_count`; `Applied_message_ids_are_capped_at_1000`.
**Prove:** `dotnet test --project tests/Ledgerline.Reporting.IntegrationTests --filter-class "*ProjectionTests"`
**Commit:** `feat(reporting): add payment timelines and merchant daily totals`

### M5-T06: Reporting API, rebuild and consistency proofs

- [ ] Done

**Spec:** `reporting.md` sections 5, 6 (Q2, Q3), 7 (lag header). **Depends:** M5-T05.
**Files:** `Projections/AccountOwnersProjection.cs`, `Features/{StatementEndpoint.cs,TimelineEndpoint.cs,DailyTotalsEndpoint.cs}`, `Consuming/LagTracker.cs`, `Infrastructure/RebuildCommand.cs` (`rebuild`: drop collections, reset the group's offsets to the beginning). Test `ReportingApiTests.cs`, `RebuildTests.cs`.
**Tests:** `Statement_is_paginated_newest_first_and_owner_only`; `Timeline_is_visible_to_both_parties_and_admin`; `Lag_header_is_present`; `Rebuild_from_offset_zero_produces_identical_documents` (export all three collections sorted by `_id` before and after, compare; exclude `updatedAt`); `Latest_statement_balance_equals_ledger_balance_after_catch_up` (runs Accounts in-process too, 200 random postings).
**Prove:** `dotnet test --project tests/Ledgerline.Reporting.IntegrationTests`
**Commit:** `feat(reporting): add query API and prove rebuild`

### M5-T07: Reconciliation: matching engine (pure)

- [ ] Done

**Spec:** `reconciliation.md` section 3 steps 5 and 6. **Depends:** none.
**Files:** Create `src/Services/Reconciliation/Ledgerline.Reconciliation/` (web project; add to solution and to `Deployables`): `Matching/{BankRecord.cs,StagedPayment.cs,StagedLedgerLine.cs,Mismatch.cs,MismatchCode.cs,ReconciliationResult.cs,Matcher.cs,SettlementFileParser.cs}`; `tests/Ledgerline.Reconciliation.Tests/` with `MatcherTests.cs`, `SettlementFileParserTests.cs`.
**Produces:** `Matcher.Match(IReadOnlyList<BankRecord> bank, IReadOnlyList<StagedPayment> payments, IReadOnlyList<StagedLedgerLine> ledger, DateOnly businessDate) : ReconciliationResult(IReadOnlyList<Guid> MatchedPaymentIds, IReadOnlyList<Mismatch> Mismatches)`; `SettlementFileParser.Parse(string csv) : IReadOnlyList<BankRecord>` throwing `SettlementFormatException` with the line number.
**Tests:** `Clean_day_has_no_mismatches`; one test per mismatch code named `Detects_<Code>`; `Compensated_topup_with_capture_and_refund_is_matched`; `Records_outside_business_date_are_ignored`; `Imbalanced_ledger_lines_give_LedgerImbalance`; parser: `Parses_spec_sample`, `Rejects_missing_column_with_line_number`, `Rejects_non_numeric_amount`, `Empty_file_with_header_is_valid`.
**Prove:** `dotnet test --project tests/Ledgerline.Reconciliation.Tests`
**Commit:** `feat(reconciliation): add matching engine and settlement parser`

### M5-T08: Reconciliation: staging consumers and schema

- [ ] Done

**Spec:** `reconciliation.md` sections 2, 6. **Depends:** M5-T03, M5-T07.
**Files:** In Reconciliation: `Infrastructure/{ReconciliationDbContext.cs,Entities/*.cs,Migrations/Initial,MigrateCommand.cs}` (Quartz tables through the SQL script shipped with `Quartz.Serialization.SystemTextJson` and the PostgreSQL job store package; verify package names on nuget.org), `Staging/{PaymentStagingConsumer.cs,LedgerStagingConsumer.cs}` (reuse the consumer loop pattern from Reporting by copying the small class; do not create a shared Kafka consumer library for two users); `deploy/compose/postgres/init-databases.sql` gains `reconciliation` login and database. `tests/Ledgerline.Reconciliation.IntegrationTests/StagingTests.cs`.
**Tests:** `Payment_events_upsert_staged_payment_with_latest_status`; `Ledger_lines_are_staged_once_with_bank_clearing_flag`; `Duplicate_records_do_not_duplicate_rows`.
**Prove:** `dotnet test --project tests/Ledgerline.Reconciliation.IntegrationTests --filter-class "*StagingTests"`
**Commit:** `feat(reconciliation): stage payment and ledger events from Kafka`

### M5-T09: Reconciliation: job, lock, run lifecycle, events

- [ ] Done

**Spec:** `reconciliation.md` sections 3, 4, 7, 8; ADR 0010. **Depends:** M5-T08, M2-T07.
**Files:** `Runs/{DailyReconciliationJob.cs,ReconciliationRunner.cs,BankSettlementClient.cs,RunLock.cs}`, contracts `Reconciliation/{ReconciliationCompleted.cs,PaymentsReconciled.cs,ReconciliationMismatchDetected.cs}` with samples, `Program.cs` (Quartz clustered ADO.NET store, messaging with outbox only). Add `Quartz`, `Quartz.Extensions.Hosting` 4.7.3. Test `RunTests.cs` (Bank Simulator in-process; Kafka records produced by the test).
**Tests:** `Clean_day_run_completes_with_zero_mismatches_and_publishes_events`; `Dropped_settlement_row_gives_LedgerEntryNotAtBank` and `Altered_amount_gives_AmountMismatch` (Bank Simulator tampering); `Two_concurrent_runs_for_same_date_produce_one_completed_run` (C1); `Rerun_of_a_date_gives_same_items` (C2); `Crashed_running_run_can_be_rerun`; `Bank_unreachable_marks_run_Failed`; `Reconciled_payment_ids_are_published_in_batches_of_500` (1 200 payments give 3 events); `Job_is_scheduled_at_0030_utc` (inspect the trigger).
**Prove:** `dotnet test --project tests/Ledgerline.Reconciliation.IntegrationTests --filter-class "*RunTests"`
**Commit:** `feat(reconciliation): run end-of-day reconciliation with Quartz`

### M5-T10: Reconciliation API and downstream consumers

- [ ] Done

**Spec:** `reconciliation.md` section 5; `payments.md` section 5 (reconciled); `notifications.md` section 2 (mismatch row). **Depends:** M5-T09.
**Files:** Reconciliation `Features/{StartRunEndpoint.cs,ListRunsEndpoint.cs,GetRunEndpoint.cs,ListItemsEndpoint.cs}`; Payments `Features/Reconciliation/PaymentsReconciledHandler.cs`; Notifications `Features/Reconciliation/MismatchDetectedHandler.cs`, template `reconciliation-mismatch`, option `Notifications:OperationsEmail`. Tests in each service's integration project: `ReconciliationApiTests.cs`, `ReconciledTests.cs`, extend `NotificationFlowTests.cs`.
**Tests:** `Start_run_requires_admin_and_returns_202`; `Items_can_be_filtered_by_code`; `PaymentsReconciled_sets_reconciled_at_once` (second event does not change the timestamp); `Mismatch_event_emails_operations_address`.
**Prove:** `dotnet test --solution Ledgerline.slnx`
**Commit:** `feat(phase2): expose reconciliation API and consume its events`

### M5-T11: Phase 2 deployment, system test, README

- [ ] Done

**Depends:** M5-T06, M5-T10.
**Files:** Dockerfiles for Reporting and Reconciliation; `compose.yaml` (profile `phase2`: both services with migrate job for Reconciliation, `Messaging__Kafka__Enabled=true` for Accounts and Payments when the profile is active through an override file `compose.phase2.yaml` or environment interpolation; choose the override file); Gateway `appsettings.json` (two Phase 2 routes); Helm `values.yaml` (`phase2.enabled`, Kafka and MongoDB infra templates, two services); `tests/Ledgerline.SystemTests/Phase2SystemTests.cs`; `README.md` (Phase 2 section, CQRS note from `reporting.md` section 8).
**Tests:** `Read_model_catches_up_and_matches_ledger_after_workload`; `Reconciliation_of_today_is_clean_after_workload_and_flags_a_tampered_row`.
**Prove:** `docker compose -f compose.yaml -f compose.phase2.yaml --profile app --profile phase2 up --build -d --wait && LEDGERLINE_SYSTEM_TESTS=1 dotnet test --project tests/Ledgerline.SystemTests --filter-class "*Phase2SystemTests"`
**Commit:** `feat(phase2): deploy reporting and reconciliation`

## Milestone 5 checklist

- [ ] Kafka event stream fed through the outbox, outage isolated from RabbitMQ (M5-T02, M5-T03).
- [ ] MongoDB read model with idempotent projections and a proven rebuild (M5-T04 to M5-T06).
- [ ] End-of-day reconciliation with a three-way match and every mismatch code tested (M5-T07 to M5-T10).
- [ ] One command still starts everything, now with the Phase 2 profile (M5-T11). **DoD: one command**
- [ ] ADR 0010 matches the code; ADR 0000 Phase 2 rows re-verified. **DoD: ADRs written**

---

# Milestone 6: Blazor UI, then optional Angular admin

The UI is a demo client. Keep it small. No business rules in the UI.

### M6-T01: Blazor project with OIDC login

- [ ] Done

**Spec:** ADR 0004 (client `ledgerline-web`); `gateway.md` (CORS not needed for server-side calls). **Depends:** M2 complete (M5 optional).
**Files:** Create `src/Web/Ledgerline.Web/` with `dotnet new blazor --interactivity Server --empty` (check the template options of the installed SDK), `Program.cs` (cookie plus OpenID Connect against Keycloak, authorization code with PKCE, save tokens, map realm roles), `Components/Layout/*`, `Components/Pages/{Home.razor,Login.razor}`; add `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.15. Add to the solution and to `Deployables`. `tests/Ledgerline.Web.Tests/` with bUnit (verify bUnit version, license, and .NET 10 support on nuget.org; add to ADR 0000).
**Tests:** `Home_shows_login_link_when_anonymous`; `Home_shows_user_name_and_roles_when_authenticated`.
**Prove:** `dotnet test --project tests/Ledgerline.Web.Tests`; manual: `dotnet run --project src/Web/Ledgerline.Web` with the stack up, log in as `alice`, name is shown.
**Commit:** `feat(ui): add Blazor app with Keycloak login`

### M6-T02: API client and wallets page

- [ ] Done

**Depends:** M6-T01.
**Files:** `Api/{LedgerlineApi.cs,AccessTokenHandler.cs,Models.cs,ApiProblemException.cs}` (typed `HttpClient` to the gateway, bearer token from the user's session, problem details mapped to `ApiProblemException`, a fresh `Idempotency-Key` per user action that is kept for retries of that action), `Components/Pages/Wallets.razor` (list, open wallet). Tests `WalletsPageTests.cs`, `LedgerlineApiTests.cs` (stub `HttpMessageHandler`).
**Tests:** `Wallets_page_lists_accounts_with_formatted_balances`; `Open_wallet_calls_api_with_idempotency_key`; `Retry_of_same_action_reuses_idempotency_key`; `Problem_details_are_shown_as_error_message`.
**Prove:** `dotnet test --project tests/Ledgerline.Web.Tests`
**Commit:** `feat(ui): add API client and wallets page`

### M6-T03: Top-up and transfer pages with status polling

- [ ] Done

**Depends:** M6-T02.
**Files:** `Components/Pages/{TopUp.razor,Transfer.razor}`, `Components/Shared/{PaymentStatus.razor,AmountInput.razor}` (`AmountInput` converts major-unit text to minor units without floating point: parse digits around the decimal separator), `Api/PaymentPoller.cs` (poll every 500 ms until final, 30 s limit).
**Tests:** `AmountInput_parses_major_units_to_minor` (theory: `"1"` 100, `"1.5"` 150, `"1.50"` 150, `"0.01"` 1, `"1.005"` invalid, `"-1"` invalid, `"abc"` invalid); `TopUp_submits_and_shows_Completed_after_polling`; `Transfer_shows_failure_reason_when_Failed`; `Poller_stops_at_final_status`.
**Prove:** `dotnet test --project tests/Ledgerline.Web.Tests`
**Commit:** `feat(ui): add top-up and transfer pages`

### M6-T04: Merchant payment pages

- [ ] Done

**Depends:** M6-T03.
**Files:** `Components/Pages/{Pay.razor,MerchantPayments.razor,PaymentDetail.razor}` (customer pays a merchant; merchant lists payments, captures fully or partially, voids, refunds; detail shows the timeline).
**Tests:** `Pay_creates_manual_capture_payment`; `Merchant_sees_capture_and_void_only_for_Authorized`; `Refund_is_offered_only_for_Captured_with_remaining_amount`; `Detail_renders_timeline_in_order`.
**Prove:** `dotnet test --project tests/Ledgerline.Web.Tests`
**Commit:** `feat(ui): add merchant payment pages`

### M6-T05: Statements, notifications, admin pages

- [ ] Done

**Depends:** M6-T04. Statement and reconciliation pages need M5; if M5 is not done, build the statement page on `GET /api/accounts/{id}/entries` and skip the reconciliation page.
**Files:** `Components/Pages/{Statement.razor,Notifications.razor,Admin/RiskRules.razor,Admin/Reconciliation.razor,Admin/Accounts.razor}` (admin pages visible only with role `admin`: edit rules, start and inspect reconciliation runs, freeze and unfreeze, trial balance).
**Tests:** `Admin_menu_is_hidden_for_customer`; `Risk_rule_edit_sends_only_applicable_fields`; `Reconciliation_page_lists_mismatches_by_code`; `Statement_paginates`.
**Prove:** `dotnet test --project tests/Ledgerline.Web.Tests`
**Commit:** `feat(ui): add statement, notifications and admin pages`

### M6-T06: UI deployment and README

- [ ] Done

**Depends:** M6-T05.
**Files:** `src/Web/Ledgerline.Web/Dockerfile`; `compose.yaml` (profile `ui`, host port 5100, matches the realm redirect URI); Helm `values.yaml` (service `web`, NodePort); `README.md` ("Demo UI" section with two screenshots in `docs/results/img/`, and a 60-second demo script: log in as `alice`, top up, pay `shop`, log in as `shop`, capture, refund).
**Prove:** `docker compose --profile app --profile ui up --build -d --wait`, then the README demo script works by hand; screenshots exist.
**Commit:** `feat(ui): deploy the Blazor app and document the demo`

### M6-T07 (optional, separate): Angular admin panel

- [ ] Done or explicitly skipped

Only if the owner decides to target postings that ask for Angular. This task is a
separate small project and does not block the definition of done.

**Depends:** M6-T05.
**Files:** `src/AdminPanel/` (Angular workspace; verify the current Angular LTS version first and record it in ADR 0000), three screens reusing the admin API routes: risk rules, reconciliation runs and items, accounts with freeze and trial balance. OIDC with authorization code and PKCE through a new public client `ledgerline-admin` in the realm. Add an ADR `0012-angular-admin.md` stating why a second UI exists.
**Tests:** component tests for the three screens with the framework's default test runner; one Playwright smoke test (login as `admin`, see the rules table).
**Prove:** `npm test -- --watch=false` in `src/AdminPanel`, and the Playwright smoke passes against the Compose stack.
**Commit:** `feat(admin): add optional Angular admin panel`

## Milestone 6 checklist

- [ ] Blazor UI covers wallets, top-up, transfer, merchant payment, capture, void, refund, timeline (M6-T01 to M6-T04).
- [ ] Admin pages cover risk rules, freeze, trial balance, reconciliation (M6-T05).
- [ ] UI starts with the Compose `ui` profile (M6-T06). **DoD: one command**
- [ ] Angular admin done or recorded as skipped (M6-T07).

---

# Definition of done for the whole project

| Requirement | Proven by | Milestone |
|---|---|---|
| Starts with one command | `docker compose --profile app up --build -d --wait && ./scripts/smoke.sh` | M1-T24, M2-T19 |
| Architecture diagram and ADRs written | `docs/design.md`, `docs/adr/0000` to `0011`, README diagram | done in design; kept current in M1-T25, M2-T19 |
| Outbox and idempotent consumer proven by tests | `OutboxTests`, `OutboxRelayTests`, `ConsumerTests`, `LedgerCommandTests` | M1 |
| Broker down test passes | `Outbox_keeps_messages_while_broker_is_down_and_delivers_after_recovery`, `Topups_accepted_during_broker_outage_complete_after_recovery`, `Broker_outage_loses_no_money` | M1-T11, M2-T17, M3-T06 |
| Service crash test passes | `Crash_after_ledger_commit_before_ack_does_not_double_post`, `Crash_after_saga_transition_commit_resumes_from_outbox`, `Crash_after_bank_capture_before_commit_does_not_capture_twice`, `Killing_payments_mid_saga_completes_after_restart` | M1-T23, M2-T12, M3-T06 |
| Duplicate delivery test passes | `Same_message_delivered_twice_is_handled_once`, `Same_operation_with_new_message_id_returns_same_reply_without_second_entry`, `Replaying_every_message_changes_nothing` | M1-T08, M1-T16, M3-T07 |
| Runs on Kubernetes (Helm, health checks) | `scripts/kind-deploy.sh`, `scripts/kind-resilience.sh`, CI job `kind` | M4 |
| OpenTelemetry traces and a dashboard screenshot in the README | `TracingTests`, `docs/results/img/*.png` | M3-T01, M3-T03, M3-T10 |
| k6 results in the README | `docs/results/k6-*.md`, README "Measurements" | M3-T10 |
| Tests and security scans run in CI | `ci.yml` jobs `build-test`, `security`, `images`, `system-tests`; `codeql.yml` | M3-T11, M3-T12 |
| "When monolith" section written | `docs/when-monolith.md` with measured numbers, linked from the README | drafted; completed in M3-T10 |

The project is finished when every row is true on the same commit.
