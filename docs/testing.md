# Testing

Strategy decisions: ADR 0008. This document lists what is tested, the exact failure
scenarios, how they run in CI, and the k6 scenarios with target metrics.

## 1. Pyramid

| Level | Projects | What | Infrastructure | Runs on |
|---|---|---|---|---|
| Unit | `*.Tests` | `Money`, ledger validation, saga `Decide` table, risk rules, template rendering, envelope and contract samples | none | every push |
| Integration | `*.IntegrationTests`, `BuildingBlocks.Messaging.IntegrationTests` | One service in-process against real PostgreSQL, RabbitMQ, Redis; failure injection at seams | Testcontainers | every push |
| Architecture | `Ledgerline.ArchitectureTests` | Service isolation, consumers only through the inbox pipeline, balances written only by the ledger | none | every push |
| System | `Ledgerline.SystemTests` | Built images under Compose, black-box over the gateway, `docker stop` and `docker kill` | Compose stack | main, nightly |
| Load | `tests/k6` | Latency, throughput, saga completion, invariants under load | Compose stack | manual, nightly smoke |

Expected proportions at the end of M3: about 150 unit, 70 integration, 10
architecture, 8 system tests. These are estimates, not targets to hit.

## 2. Shared test infrastructure

Project `tests/Ledgerline.Testing` (created in M1, referenced by integration and
system tests):

- `InfrastructureFixture`: starts PostgreSQL, RabbitMQ, and Redis once per test
  assembly. Images and tags are the ones in `compose.yaml`.
- `DatabaseFactory.CreateAsync(service)`: creates a fresh database from a migrated
  template (`CREATE DATABASE ... TEMPLATE ...`) per test class.
- `ServiceHost<TProgram>`: wraps `WebApplicationFactory`, points configuration at the
  fixture, exposes `Services` and an authenticated `HttpClient` using a locally
  signed test JWT.
- `BrokerProbe`: declares an exclusive queue bound to given routing keys and collects
  messages; `PublishAsync(envelope)` for injecting messages, including duplicates.
- `Eventually.ShouldAsync(condition, timeout)`: polls every 50 ms, default timeout
  10 s. Fixed delays (`Task.Delay` as a wait) are forbidden in tests by an analyzer rule.
- `LedgerAssert.InvariantsHoldAsync(accountsDb)`: the four checks from ADR 0008
  (zero sum per currency, balanced entries, no negative wallet, projections equal
  replay). Called at the end of every test that moves money.
- Fault seams, replaced through DI in tests:

| Seam (interface) | Used to simulate |
|---|---|
| `IOutboxPublisher` | Broker rejecting or timing out publishes |
| `IAckStrategy` | Crash after commit, before acknowledge |
| `ICrashPoint` (`void Hit(string name)`; no-op in production) | Crash at a named point; the test implementation stops the host without graceful shutdown and blocks the calling thread |
| `IBankClient` | Bank outcomes without HTTP (integration tests that are not about HTTP) |

Named crash points in production code: `ledger.after-commit`,
`saga.after-transition-commit`, `bank.after-response`, `relay.after-publish`.

## 3. Failure scenarios (definition of done)

Each scenario has an integration test (deterministic, in pull requests) and, for the
first three, a system test (real process kill, on main and nightly). Test names are
exact; the plan refers to them.

### F1. Broker down

Claim: while RabbitMQ is unavailable, requests are accepted, nothing is lost, and
everything completes after the broker returns.

| Test | Steps | Assertions |
|---|---|---|
| `Messaging.IntegrationTests`: `Outbox_keeps_messages_while_broker_is_down_and_delivers_after_recovery` | Stop the RabbitMQ container. Commit 20 state changes with outbox rows. Assert 20 rows pending and relay alive. Start the container. | All 20 delivered exactly once to the probe, in `sequence` order; all rows processed; `attempts > 0` on at least one row |
| `Payments.IntegrationTests`: `Topups_accepted_during_broker_outage_complete_after_recovery` | Stop RabbitMQ. POST 10 top-ups (all `202`). Start RabbitMQ. Probe answers risk and ledger commands. | 10 payments `Completed`; no payment in `ManualReview`; `step_attempt` was not consumed by the outage |
| `SystemTests`: `Broker_outage_loses_no_money` | `docker compose stop rabbitmq`. 50 top-ups and 50 transfers through the gateway. `docker compose start rabbitmq`. Wait until no payment is `Processing`. | Every payment is final; sum of wallet balances equals sum of completed top-ups; trial balance zero |

### F2. Service crashes mid-flow

Claim: killing a service at any point of a flow never loses or duplicates money. The
flow finishes after restart.

| Test | Crash point | Assertions |
|---|---|---|
| `Accounts.IntegrationTests`: `Crash_after_ledger_commit_before_ack_does_not_double_post` | `ledger.after-commit` | After restart the command is redelivered; exactly one journal entry for the `operationId`; exactly one succeeded reply observed after deduplication by `operationId`; invariants hold |
| `Payments.IntegrationTests`: `Crash_after_saga_transition_commit_resumes_from_outbox` | `saga.after-transition-commit` | After restart the next command is published once; the payment completes |
| `Payments.IntegrationTests`: `Crash_after_bank_capture_before_commit_does_not_capture_twice` | `bank.after-response` | After restart the bank call is repeated with the same idempotency key; Bank Simulator has one capture; payment completes |
| `Messaging.IntegrationTests`: `Crash_after_publish_before_marking_processed_republishes_and_consumer_deduplicates` | `relay.after-publish` | The message is published twice; the handler's effect is committed once; `inbox.duplicates` incremented |
| `SystemTests`: `Killing_payments_mid_saga_completes_after_restart` | `docker kill -s KILL` of the payments container while 100 top-ups are in flight, bank fault `Delay 2000 ms` to widen the window; `docker compose start payments` | All 100 final; bank captures equal ledger top-up entries plus bank refunds; no wallet credited twice; trial balance zero |
| `SystemTests`: `Killing_accounts_mid_posting_completes_after_restart` | Same with the accounts container during 200 transfers | All final; invariants hold |

### F3. Message delivered twice

Claim: a duplicate delivery never causes a second effect.

| Test | Steps | Assertions |
|---|---|---|
| `Messaging.IntegrationTests`: `Same_message_delivered_twice_is_handled_once` | Publish the same envelope twice | Handler effect once; second delivery acknowledged; duplicate metric is 1 |
| `Messaging.IntegrationTests`: `Same_message_delivered_concurrently_to_two_consumers_is_handled_once` | Two consumer instances, same envelope published twice at once | One inbox row; effect once |
| `Accounts.IntegrationTests`: `Same_operation_with_new_message_id_returns_same_reply_without_second_entry` | Send `credit-top-up` twice with the same `operationId`, different `messageId` | One journal entry; two identical succeeded replies |
| `Accounts.IntegrationTests`: `Duplicate_reserve_then_capture_commands_keep_hold_consistent` | Each of reserve and capture delivered twice | One hold, `Captured`; balances as after one of each |
| `Payments.IntegrationTests`: `Duplicate_and_late_replies_do_not_move_a_final_payment` | Deliver `ledger-operation-succeeded` twice, then a stale `risk-approved` | Status unchanged; `saga_steps` has no extra rows |
| `Risk.IntegrationTests`: `Evaluating_same_payment_twice_counts_once` | Same `paymentId` twice | One decision; daily total counted once; velocity count 1 |
| `SystemTests`: `Replaying_every_message_changes_nothing` | A test-only shovel re-publishes every message seen on all three exchanges during a 60 s mixed workload | Final balances and payment statuses equal the run's expected values; trial balance zero |

### F4. Concurrent transfers on the same account

Claim: concurrent debits never overdraw and never deadlock.

| Test | Steps | Assertions |
|---|---|---|
| `Accounts.IntegrationTests`: `Concurrent_debits_never_overdraw` | Wallet with 10 000. 50 concurrent `transfer-funds` of 500 to 50 different accounts | Exactly 20 succeed, 30 rejected `InsufficientFunds`; balance 0; invariants hold |
| `Accounts.IntegrationTests`: `Opposite_direction_transfers_do_not_deadlock` | A to B and B to A, 200 each, concurrently, 8 consumers | All succeed within 30 s; no `40P01` in logs; balances as computed |
| `Accounts.IntegrationTests`: `Concurrent_postings_keep_stream_versions_gapless` | 500 mixed postings on 10 accounts | For each stream, versions are 1..n without gaps; projections equal replay |
| `Payments.IntegrationTests`: `Capture_and_void_race_has_exactly_one_winner` | 20 pairs of simultaneous `POST capture` and `POST void` | Each payment ends `Captured` or `Voided`; the losing call got `409` |
| `Risk.IntegrationTests`: `Concurrent_evaluations_respect_daily_limit` | Limit fits 3 of 10 concurrent evaluations | Exactly 3 approved |
| `Payments.IntegrationTests`: `Same_idempotency_key_sent_concurrently_creates_one_payment` | 10 identical POSTs at once | One payment row; responses are `202` or `409 request-in-progress`; later replay returns the same ID |

### F5. Other failure tests (not in the definition of done, in the plan)

- `Poison_message_goes_to_dead_queue_after_max_attempts`
- `Unknown_message_type_is_dead_lettered_immediately`
- `Bank_timeout_with_success_is_resolved_by_lookup` (fault `SuccessThenTimeout`)
- `Ledger_rejection_after_bank_capture_refunds_at_bank` (flow 10.5 of the design)
- `Exhausted_compensation_ends_in_manual_review`
- `Redis_down_idempotency_falls_back_to_database`
- `Redis_down_velocity_falls_back_to_database`
- `Authorization_expiry_releases_hold`
- `Projections_rebuilt_from_events_equal_live_projections`

## 4. CI

Workflow `.github/workflows/ci.yml` (exists, grows per milestone):

| Job | Trigger | Content | Budget |
|---|---|---|---|
| `build-test` | push, pull request | restore, build (warnings as errors), unit, architecture, integration tests | 10 min |
| `compose-validate` | push, pull request | `docker compose config`, Helm lint and template (from M4) | 1 min |
| `security` (M3) | push, pull request | Dependency review, `dotnet list package --vulnerable --include-transitive`, gitleaks, CodeQL (C#) | 8 min |
| `images` (M3) | main | Build service images, Trivy scan, fail on HIGH or CRITICAL with a fix available | 10 min target; job timeout 15 min is the ceiling |
| `system-tests` (M3) | main, nightly | Run `Ledgerline.SystemTests` with `LEDGERLINE_SYSTEM_TESTS=1`; the harness starts its own Compose project `ledgerline-systest` (`up --build --wait`), and `LEDGERLINE_SYSTEM_TESTS_KEEP=1` keeps it up so container logs can be uploaded on failure | 20 min target; job timeout 30 min is the ceiling |
| `k6-smoke` (M3) | nightly | 60 s smoke scenario with thresholds; upload summary JSON | 5 min target; job timeout 30 min is the ceiling |
| `kind` (M4) | main | kind cluster, Helm install, readiness, smoke, pod-delete test | 15 min |

Integration tests run on `ubuntu-latest`, which provides Docker for Testcontainers.
System tests are skipped unless `LEDGERLINE_SYSTEM_TESTS=1` is set. Test selection
uses the Microsoft.Testing.Platform options `--filter-class`, `--filter-method`, and
`--filter-namespace` (verified with xunit.v3 4.0.3 on 2026-11-13).

Flaky test policy: a test that fails without a code change is quarantined the same day
with a tracking issue. Retries are not used to hide flakiness.

## 5. k6 scenarios

Scripts in `tests/k6/`. Run against the Compose stack through the gateway:
`docker compose --profile app up -d` then
`docker run --rm --network ledgerline_default -v ./tests/k6:/scripts grafana/k6:2.4.0 run /scripts/<scenario>.js`.
k6 2.x script API compatibility with the examples below is not verified; check at M3.

Setup for every scenario: obtain tokens for seeded users through the `ledgerline-k6`
client, create 200 customer wallets and 10 merchant wallets, top each customer up.

| Scenario | Load | What it shows |
|---|---|---|
| `smoke.js` | 5 VUs, 60 s, mixed | The stack works; used in CI |
| `topup-steady.js` | 50 requests/s, 5 min | Full saga with the bank step |
| `transfer-steady.js` | 100 requests/s, 5 min, random pairs | Ledger throughput without the bank |
| `transfer-hot-account.js` | 100 requests/s, 3 min, all transfers into one merchant | Row-lock contention on a hot account |
| `payment-mix.js` | 50 requests/s, 5 min: 60% authorize and capture, 20% authorize and void, 10% refund, 10% top-up | Realistic mix, system accounts under load |
| `spike.js` | 20 to 300 requests/s in 10 s, hold 60 s, back | Backlog and recovery; outbox lag |
| `broker-outage.js` | 50 requests/s, 3 min, RabbitMQ stopped for 30 s in the middle (manual or scripted) | Acceptance continues; recovery time |

Each script records, besides HTTP metrics, a custom trend `saga_completion_ms`
(POST accepted until `GET /payments/{id}` returns a final status, polled every 100 ms)
and ends with a teardown check that calls `GET /api/ledger/trial-balance`.

### Target metrics

These are **targets to validate**, chosen before any measurement. The README will
show the measured values, the hardware, and where a target was missed.

| Metric | Target |
|---|---|
| `http_req_failed` (excluding expected 4xx) | < 0.1% |
| POST accept latency, p95 | < 150 ms |
| POST accept latency, p99 | < 400 ms |
| `saga_completion_ms` transfer, p95 | < 1 000 ms |
| `saga_completion_ms` top-up, p95 | < 2 000 ms |
| `ledgerline.outbox.lag`, p95 at steady load | < 500 ms |
| Payments in `ManualReview` after a clean run | 0 |
| Trial balance after every run | 0 |
| Sustained throughput on one laptop, transfers | >= 100 requests/s |
| Recovery after a 30 s broker outage (backlog drained) | < 60 s |

Reported with every result: CPU model, cores, RAM, Docker resource limits, replica
counts, commit hash, date. Results from a laptop running the load generator and the
system together are not capacity numbers and the README says so.

## 6. What is not tested

- Infrastructure failover (PostgreSQL or RabbitMQ node loss with replicas). Single
  nodes only.
- Network partitions between services and the database. `docker stop` and `kill`
  cover crash-stop failures, not slow or lossy networks. Toxiproxy would be the tool;
  listed as possible future work.
- Security testing beyond the CI scans. No penetration test, no threat model review.
- Long-running soak tests.
