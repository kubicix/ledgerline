# M3 implementation decisions

These historical decisions preserve their rationale and the cost if wrong.
Entries marked superseded were later withdrawn or reverted; the current state is
stated in the entry. The M2 equivalent is [M2 decisions](m2-decisions.md).

## Rulings

- Setup: Ruling: The task-brief script cannot parse `### M3-TNN` headings, so each brief was built by a local script from the preamble, the task text, the global constraints, the task protocol, all traps and the spec excerpts. Cost if wrong: briefs carry slightly more trap text than needed.
- Setup: Ruling: Implementers never commit. The orchestrator stages the exact task files, reviews `git diff --cached -U10`, and commits once with the plan's message after review is clean. Post-commit findings go to `fix:` commits. Cost if wrong: none, the history matches the M2 shape.
- Baseline: Ruling: The owner said a resolved flake counts as passed and the full suite runs only when truly required. The baseline was 504 of 505 with one Accounts integration failure that passed on a focused rerun (66 of 66), so work proceeded to Task 1 and the full suite ran again only at the final gate. Cost if wrong: an intermittent Accounts flake stays undiagnosed until the final run; the suite already took 11m19s before M3, longer than the roughly 11 minute budget note.
- Task 1: Ruling: Meters are subscribed with `AddMeter("Ledgerline", "Ledgerline.*")`, the BCL `System.Runtime` meter and the ASP.NET Core instrumentation metrics, with no rename. This also answers the spec's "runtime metrics" wording without a new package. Cost if wrong: the rename happens in Task 2 instead.
- Task 1: Ruling: The OTLP exporter is registered only when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured, so test hosts never export to a live localhost:4317. Cost if wrong: a service started outside Compose without the variable exports nothing.
- Task 1: Ruling: Spans carry both `messaging.system` and the semantic-convention v1.45.0 `messaging.system.name`, because the messaging conventions are still in Development. The destination is `{exchange}:{type}` on send and `{exchange}:{type}:{service}.inbox` on process. Cost if wrong: string changes only.
- Task 1: Ruling: `AddLedgerlineTelemetry` extends `IHostApplicationBuilder`. Web keeps a literal `"Ledgerline.Messaging"` constant because Web must not reference Messaging. Cost if wrong: the two constants can drift; TracingTests guard it.
- Task 1: Ruling: Span errors carry `error.type` only, never the exception message, to avoid leaking personal data. Cost if wrong: less detail in Tempo.
- Task 1: Ruling: The implementer found a flake, `Outbox_keeps_messages_while_broker_is_down_and_delivers_after_recovery` (order `[2..20,1]` once in three runs), with a confirmed pre-existing M1 cause: the relay gated on the application clock while its SELECT used the Postgres `now()` of the transaction start. It was fixed in a separate `fix:` commit after Task 1 with a deterministic regression test, because the flaky-test policy treats a flake as a defect with a cause. Cost if wrong: a one-line selection change in the relay; ordering is what that test asserts.
- Task 1: Ruling: A stale 0-byte `.git/index.lock` with no git process running was removed before the commit, as git's own message instructs. Cost if wrong: none.
- Task 1: Ruling: The relay flake fix keeps the spec section 7 SELECT on `now()`. The head gate (code only, not in the spec) moves onto the database clock inside the same transaction, so gate and SELECT agree. Cost if wrong: one extra predicate in the head query.
- Task 1: Ruling: A flake note on two consumer tests under host load (`TopologyTests.Retry_queue_dead_letters_back_to_inbox_after_ttl` and `RetryAndDeadLetterTests.Failing_handler_is_retried_through_retry_queue_with_incremented_attempt`, attempts `[1,2,2,3]`) was diagnosed as the 5 second confirm timeout in `ConsumerHost.ForwardSafelyAsync` plus nack and requeue under load, which forwards at-least-once and duplicates one attempt. The inbox still has a single effect, so this is pre-existing M1 test strictness and not a money defect. It is deferred. Cost if wrong: an intermittent red on loaded CI runners.
- Task 2: Ruling: Messaging metrics are tagged `ledgerline.service`. They use a static `Meter("Ledgerline")` (process wide) while saga and ledger metrics use `IMeterFactory`. Cost if wrong: messaging metrics cannot be isolated per host in future in-process tests.
- Task 2: Ruling: Outbox lag is measured from `OccurredAt` (enqueue time on the application clock), because commit time is not stored and no schema change is allowed. It overstates by the enqueue-to-commit gap (1 to 10 ms) plus any skew between hosts, and `Math.Max(.,0)` hides negative skew. Cost if wrong: the lag p95 is slightly high.
- Task 2: Ruling: Posting duration is recorded in `LedgerOperationRunner`, which covers all six message-driven postings. Direct `ILedger.PostAsync` callers (the Task 9 benchmark and test helpers) are not measured; the benchmark times itself. Cost if wrong: benchmark numbers are not in the histogram.
- Task 2: Ruling: The imbalance refresh interval is configurable through `LedgerMetricsOptions` (default 15 seconds) so tests need not wait. Cost if wrong: one options class.
- Task 2: Ruling: The "delivered twice is handled once" assertion required by trap 6 was added to `ConsumerTests.Same_message_delivered_twice_is_handled_once`; the crash test already asserted it. Cost if wrong: none.
- Task 2: Ruling: The posting duration outcome tag uses the `LedgerOperation` names Succeeded, Rejected and Failed. `LedgerMetrics.Refreshes` is a public read-only counter so the test needs no `InternalsVisibleTo`. Cost if wrong: one public property.
- Task 3: Ruling: The plan's series name `ledgerline_outbox_lag_seconds_count` matched reality. Counters gain a `_total` suffix; the messaging label is `ledgerline_service` while saga and ledger series use `service_name`. Dashboards are provisioned at `/otel-lgtm/grafana/conf/provisioning/dashboards/` (inspected in the image) with the JSON mounted at `/etc/ledgerline/dashboards`. Cost if wrong: the dashboard is not provisioned, which `check-dashboard.sh` would catch.
- Task 3: Ruling: Saga outcome and manual-review panels show totals, not rates, because short smoke runs produce a single sample. Cost if wrong: panel semantics change only.
- Task 4: Ruling: The extra test `Documented_failure_test_names_are_unique` (five lines) stays, because it guards the inventory list itself against a duplicate or a typo. Cost if wrong: one unrequested test.
- Task 4: Ruling: The Task 4 commit was held until Task 3 committed, to keep plan order, because Task 3 was staged and blocked on port 3000. Cost if wrong: none.
- Task 5: Ruling: The collection class is named `ComposeTests` (CA1711) and `ComposeStack` keeps the plan's name with a CA1711 suppression. Cost if wrong: one suppression.
- Task 5: Ruling: Later `UpAsync` calls run `up -d --wait`, which re-runs the idempotent migrate and rabbitmq-init jobs (about 19 seconds). Cost if wrong: slower test setup.
- Task 6: Ruling: The Risk velocity rule is raised at runtime through the supported Risk admin API, with no seed or code change. Money invariants do not depend on velocity, the alternative makes the test vacuous (everything Rejected) or takes ten minutes or more, and the harness restores the seeded 10 per 60 seconds on dispose. Cost if wrong: the test runs under a non-default Risk threshold.
- Task 6: Ruling: Bulk calls cap at 8 open POSTs at a time. The spec fixes counts, not HTTP concurrency, and Postgres `max_connections=100` against a Npgsql pool of 100 per service gave error 53300 at burst. Cost if wrong: less HTTP burst than a 100-at-once reading.
- Task 6: Ruling: The bank fault is Delay 2000 ms with count 5, because consumer dispatch concurrency is 1 and prefetch is 16. Cost if wrong: a narrower kill window.
- Task 6: Ruling: No double credit is counted per `payment_id`, which is stronger than per `operation_id` (unique by constraint). Cost if wrong: none.
- Task 7: Ruling: No RabbitMQ port override is needed, because `compose.yaml` already publishes 5672 for every project. Cost if wrong: none, the spec intent is met.
- Task 8 prep: Ruling: Postgres sizing is a separate commit, `fix: size Postgres connections for burst load` (owner approved): `max_connections=300` and `Maximum Pool Size=40` on the five application services. Cost if wrong: a configuration revert.
- Task 8 prep: Ruling: The orchestrator reviewed the 12-line configuration diff directly, since it contains no logic. Cost if wrong: the final review would catch it.
- Task 8: Ruling: k6 thresholds are unchanged and not gated. The smoke run was accepted on a passing run, because the thresholds are the tests and misses on this host are a hardware artifact proven in the stall report; misses are published in Task 10. There is no code mitigation now, because pipelined confirms carry an ordering caveat. Cost if wrong: local smoke is flaky about 50% of the time, and the CI Linux runner decides.
- Task 8: Ruling: Virtual user `n` maps to customer `(n-1) mod 200`. Cost if wrong: none stated; the mapping spreads load across the 200 realm customers.
- Task 8: Ruling: Review minors 1, 2, 5, 6 and 8 were fixed before the commit, because Task 10 measures with these scripts: `SAGA_TIMEOUT_MS` is clamped to at least the largest target plus margin, `gracefulStop` is at least the saga timeout plus margin, the `k6.ps1` long-flag note and the missing-docker exit 1, and a header wording fix. Cost if wrong: small JavaScript and PowerShell edits only.
- Task 8: Ruling: The smoke proof for Task 8 is the passing run from before the fix round, which only raised a timeout floor and added `gracefulStop`. The scripts were re-exercised by the performance work and Task 10. Cost if wrong: Task 10 finds a script regression.
- Task 9: Ruling: `LocalTransferBaseline` ends with `LedgerAssert.InvariantsHoldAsync`, per the global constraint that every money-moving test ends with invariants. Cost if wrong: a few seconds per benchmark run.
- Task 9: Ruling: Saga p95 thresholds stay on the spike and outage scenarios, plus `outage_recovery_ms` max under 60000, since all are testing.md targets and misses are published. The `lib/saga.js` and `lib/api.js` extensions and `scripts/broker-outage.sh` are accepted as needed support files, with no PowerShell twin because the plan does not ask for one. The benchmark proof uses `--output Detailed` to show the numbers. Cost if wrong: extra support files beyond the plan's list.
- Task 9: Ruling: Review Important finding I1 (the teardown restored the velocity rule while a backlog was still draining, so 14,572 spike transfers were rejected for velocity) was fixed in the Task 8 file `lib/setup.js` inside Task 9. A bounded drain wait now runs before the restore and leaves a sentinel if the deadline passes. This honors the spec intent of a trial balance after every run and gives clean spike drain numbers. Cost if wrong: a longer teardown only.
- Task 11: Ruling: Phase 1 (static, no Docker) ran while Task 9 waited for Docker, because its files are disjoint from Task 9 and the performance work. The Task 11 commit still lands after Task 10. Cost if wrong: commit order differs from plan order for the work, not for the commits.
- Task 11: Ruling: The realm file allowlist in `.gitleaks.toml` is one anchored regex scoped to that one path instead of 214 literal values. Cost if wrong: low, it admits only `<known user>_dev` strings in that file.
- Task 11: Ruling: CodeQL lives in its own `codeql.yml` workflow although testing.md section 4 lists it inside the `security` job. Cost if wrong: one documentation row, or a move of steps.
- Task 11: Ruling: `GITLEAKS_ENABLE_COMMENTS` is false so the `security` job needs no `pull-requests: write`. Findings still show in the job summary and the SARIF artifact. Cost if wrong: no inline PR comments.
- Task 11: Ruling: gitleaks is pinned to 8.30.1 in both CI and the local image tag instead of `:latest`, so local proof and CI use the same allowlist semantics. Cost if wrong: manual bumps, since Dependabot does not track it.
- Task 11: Ruling: Dependabot's `docker` ecosystem covers the six service Dockerfiles only. Image tags in `compose.yaml` would need the `docker-compose` ecosystem, which was not requested and not added. Cost if wrong: Compose image tags are not auto-updated.
- Task 11: Ruling: The dependency-review step moved to the last step of the `security` job (reorder only, no `continue-on-error`), so the other scans still run if GitHub Advanced Security blocks it. Whether a red GHAS run is acceptable stays with the owner. Cost if wrong: none, step order only.
- Task 10: Ruling: Task 10 runs used `DRAIN_WAIT_S=60` (shortened at the owner's request). Each run uses a fresh throwaway project that reseeds velocity, and the trial balance is zero at any instant. Cost if wrong: the ManualReview database check may see a partial backlog, which the report notes.
- Task 10: Ruling (superseded, reverted): raise the otel-lgtm `mem_limit` and `memswap_limit` from 4g to 6g, as a separate commit, because the OOM was judged a regression of the 4g cap. The 6g attempt still ran out of memory (the collector grows about 4.6 GiB per minute under transfer-steady, at 580 to 720 percent CPU, while tmpfs used only about 0.28 GiB), so `compose.yaml` stayed at 4g and the published raw JSON was restored. Cost if wrong: 2 GiB more VM memory reserved for telemetry, never merged.
- Task 10: Ruling: No further OOM fix in M3. A collector-side `memory_limiter` or sampling is new configuration beyond the plan, one fix attempt failed, and the root cause is collector growth, not the cap. Task 10 is published with an honest partial-window dashboard and the OOM finding, and the follow-up is an owner item. Cost if wrong: dashboard evidence is weaker until a follow-up.
- Progress mod: Ruling: The plan-progress bar needs `### Task N:` headings but `docs/plan.md` uses `### M3-TNN:`, so a scratch shim with the same titles and ids 1 to 13 stands in. Cost if wrong: the bar only.
- Task 12: Ruling: The Prove scans the six `ledgerline-<service>:local` images, not a single untagged image. Cost if wrong: none functionally; an untagged reference would not exist.
- Task 12: Ruling: Trivy is pinned to 0.70.0 locally (`aquasec/trivy:0.70.0`) and `version: v0.70.0` in CI, the action's default, instead of the newest 0.76.2 or `latest`. Cost if wrong: an older Trivy might miss newer detection fixes; a bump is a one-line change in two places plus the ADR.
- Task 12: Ruling: The `system-tests` job does not pre-start the stack, although testing.md section 4 shows `up --build -d`. The harness owns `up --build --wait` under its own `ledgerline-systest` project and refuses any other Ledgerline project; `LEDGERLINE_SYSTEM_TESTS_KEEP=1` keeps the stack for log upload. Cost if wrong: none for CI; testing.md wording differs from the job.
- Task 12: Ruling: The concurrency group is `ci-${{ github.event_name }}-${{ github.ref }}` so a push to main no longer cancels a running nightly on the same ref. Cost if wrong: push and nightly runs on main may run at the same time on separate runners, which is harmless.
- Task 12: Ruling: The nightly also runs `build-test` and `security`, because `schedule:` is workflow level and no `if:` was added to existing jobs. The gitleaks PR scan and dependency review are skipped there. Cost if wrong: about 20 extra runner minutes a night.
- Task 12: Ruling: Job timeouts are `images` 15, `system-tests` 30 and `k6-smoke` 30 minutes (k6 step 15) against testing.md budgets of 10, 20 and 5. A cold runner builds six .NET images first, the harness `UpTimeout` is 20 minutes, and k6 smoke needs build, Keycloak start, `up --wait` and a setup with `setupTimeout` of 600 seconds before its 60 second load phase. Cost if wrong: a hung job burns up to 30 runner minutes instead of failing early.
- Task 12: Ruling: The nightly cron is `17 2 * * *` (02:17 UTC), off the hour to avoid GitHub's top-of-hour scheduling delays. Cost if wrong: none.
- Task 12: Ruling (superseded, reverted in 487d346): update testing.md section 4 so the system-tests and k6 rows match the workflow (harness owns `up`, budget is a target and the timeout a ceiling). The final review rejected it because the definition of done forbids editing `docs/testing.md`; see the Final ruling below. Cost if wrong: a documentation edit only.
- Task 13: Ruling: The README lists five scans (dependency check, gitleaks, dependency review, CodeQL, Trivy) because the brief's own list names five, although the plan text says four. Cost if wrong: README wording.
- Final: Ruling: The Task 12 testing.md ruling is withdrawn and `docs/testing.md` was reverted byte-identical to 1e19932 in the fix wave (487d346). The deviations it covered are recorded below, spec unchanged, and the owner decides whether to amend the spec. Cost if wrong: none.
- Final: Ruling: The orchestrator re-read the full 13-line documentation and configuration fix wave itself instead of dispatching a scoped re-review, because it changes no logic and the Fable audit followed. Cost if wrong: a wording slip in documentation.
- Final: Ruling: Fable audit Important finding I1 (`Broker_outage_loses_no_money` was blind to symmetric transfer loss or duplication) was fixed with a test-only assertion that counts journal entries per TopUp and Transfer, mirroring the kill tests (6989667). Minor findings M1 to M5 are recorded below as hardening. Cost if wrong: the owner may want M2 and M4 pulled into the M4 milestone.
- Final: Ruling: After the audit fix, two 5x runs of `FailureSystemTests` gave 3 of 5 and 4 of 5. In all three failures the killed service was never restarted: its log shows one "Application started", and `docker compose start --wait` reported the container `exited (137)` with no "Starting" line. No money assertion failed. The cause is a harness race: `docker compose kill` can return before Docker marks the container exited, so `start` skips it and `--wait` watches it die from the earlier SIGKILL. `ComposeStack.KillAsync` now waits, bounded at 30 seconds, until no container of the service is running (92c3595), and the 5x proof was run again. Cost if wrong: one more 5x cycle.

## Owner decisions

- Postgres connections: raise `max_connections` to 300 and set `Maximum Pool Size=40` on the five application services (test database, approved). It was raised because a 100-POST burst exhausted 100 shared connections against a Npgsql default pool of 100 per service.
- Realm load users: add 200 customers and 10 merchants to the Keycloak realm (development passwords) so the testing.md section 5 setup applies as written, rather than amending the spec's wallet counts. One wallet exists per user, type and currency, and the two seeded customers hit the gateway per-user rate limit (86 to 94 percent of requests got 429).
- Slow host: the owner said to "handle slow-host problems as a senior engineer", with restarts of containers allowed as long as behavior stays correct and Docker Desktop and WSL are never restarted. This led to the performance change in e06fedc: telemetry data on tmpfs and a memory cap on otel-lgtm, which cut otel-lgtm disk writes from 515 MB to 1 to 3 MB and relay lag from an average of 3 seconds to 0.5 seconds. Postgres `commit_delay` showed no gain and was reverted.
- Task 10 drain wait: shorten the post-run drain wait (`DRAIN_WAIT_S=60`).
- Also decided in chat: a resolved flake counts as passed, the plan runs continuously without per-task pauses, and unused volumes of this project's throwaway proof projects (not the default `ledgerline` project) may be deleted without asking.

## Spec deviations recorded, spec unchanged

`docs/testing.md`, `docs/design.md`, `docs/specs/*` and the ADRs were left as written, except ADR 0000 version updates and ADR 0007, which now records the hot-account decision (accept, do not shard).

- CI job timeouts are 15 (`images`), 30 (`system-tests`) and 30 (`k6-smoke`) minutes against the testing.md section 4 budgets of 10, 20 and 5.
- The system-test harness starts its own `ledgerline-systest` Compose project (set `LEDGERLINE_SYSTEM_TESTS_KEEP=1` to keep it for logs) instead of the job running `docker compose --profile app up --build -d`.
- CodeQL runs in `codeql.yml`, not in the `security` job as testing.md lists it.
- Five scans run where the plan says four (dependency check, gitleaks, dependency review, CodeQL, Trivy).
- ADR 0004 seeded-user text is stale after the 200 customer and 10 merchant load users. The README is updated; the ADR is not editable under the definition of done.

## Deferred minor findings

Task 1 and relay fix:
- Multi-relay snapshot window (the ADR 0003 caveat), a redundant helper, and the app clock used for backoff and `ProcessedAt` (pre-existing).
- Two intermittent consumer tests under host load (see the Task 1 flake ruling).

Task 2:
- The pending gauge freezes on errors other than `PublishFailed` (panels use max by service).
- `RelayOptions.Service` defaults to an empty string for hand-built relays.
- Saga duration includes the merchant wait and refunds are unmeasured (the panel is per kind).
- `TimeoutTests.Topups_accepted_during_broker_outage_complete_after_recovery` failed once in a combined run and passed alone 8 of 8 (a 30 second relay backoff against a 30 second wait is suspected).

Task 5:
- `DisposeAsync` can throw a `TimeoutException` on down, `WaitUntilNothingInFlightAsync` throws on a Postgres connection blip, and one attempt can overrun `RetryWindow`.

Task 6:
- The kill margin is about 1.3 seconds (it fails loudly, not vacuously), a `finally` `StartAsync` can mask the primary exception, and the Smoke-after-failure ordering was not exercised live.

Task 7:
- Same-id dedup is not observed directly and `inbox.retry` depth is not in quiescence or assertions.
- Holds (`HeldMinor`) are not asserted back to their start.
- `ReplayShovel.DisposeAsync` `Eventually` can mask the primary failure, and a `committed > 0` guard is redundant.
- The `local.events` durable queue has no consumer or max length and grows about 2,000 messages per run on a kept volume (pre-existing).

Task 8:
- Non-Completed finals are not gated, ManualReview is checked client side only (Task 10 confirmed it from the database), a same-day export overwrites the earlier one, and the teardown restore can run before a backlog drains.
- Throughput of 100 requests per second or more has no threshold (measured from accepted transfers and `dropped_iterations`).

Task 9:
- `api.js` `getTrialBalance` lost a space, the `LocalTransferBaseline` comment "no row contention by construction" overstates, and `broker-outage.sh` `stamp()` needs GNU `date %3N`.
- `DRAIN_WAIT_S` of 900 is shorter than the spike drain on this host (about 27 minutes), and teardown can take up to 17 minutes after a backlog.

Task 10:
- Wording "each of today's runs" versus the unknown Task 9 OOM state, and ADR 0007 "6 to 7 cores" versus the measured 580 to 720 percent. Both were reworded in 487d346; the cheap items from the review minors (truncation values, container count, a row label, the setup-inclusive window) were folded into the fix round.

Task 11:
- `ci.yml` `dotnet list` output is lost if the command itself errors.
- The gitleaks realm regex was slightly broader than the exact values (narrowed in 487d346).
- testing.md section 4 still lists CodeQL under the `security` job (a recorded deviation).

Task 12:
- The smoke artifact glob can pick the committed `smoke-2026-11-25.json`, Trivy database rate limits are only mitigated, and `upload-artifact@v7` is a major tag (repository convention).

Task 13:
- The gitleaks row "full git history" was overstated on pull requests (fixed in 487d346), and the dependency review "licenses" wording comes from the action's defaults.

Final review (Opus):
- M1: the otel-lgtm comment in `compose.yaml` was stale (fixed). M2: saga duration window semantics (recorded). M3: gitleaks regex broader than the exact values (narrowed).

Final audit (Fable), hardening items M1 to M5:
- M1: the relay head gate is not multi-replica safe (speculative; one replica runs today).
- M2: T07 inbox dedup is not observed directly.
- M3: the T07 journal check covers only the expected entries, and `HeldMinor` is unchecked.
- M4: the outbox lag histogram double-counts on a batch rollback.
- M5: gauges go stale when their query fails.

## Open owner items

- The repository is private. Dependency review and the CodeQL result upload need GitHub Advanced Security on private repositories, so those jobs may run red after the push. Either make the repository public or accept the red runs.
- otel-lgtm: the collector runs out of memory under steady load even at 6g. A collector-side `memory_limiter` or sampling is the likely fix and is new configuration beyond the M3 plan.
- The relay confirms each publish serially, which caps throughput. Pipelined confirms would raise it, with the ordering caveat that confirms must still be applied in sequence.
- Host disk: fsync stalls of about 17 seconds every 55 to 90 seconds on the DRAM-less NVMe (about 84 percent full), reproduced without Ledgerline, make the local k6 targets miss. The Linux CI runner decides.
- The durable `local.events` queue grows on kept volumes (see Task 7).
- Mailpit persistence: it keeps its database on a named volume (an M2 ruling after a restart lost emails) and writes 110 to 180 MB per session. Moving it to tmpfs would lose emails on restart, so it was left for the owner.
- EF Core and ASP.NET log at Information level, about 170 MB per session.
- The `LedgerAssert` failure message may print contact emails (pre-existing M1 and M2 helper).
- The full suite took 10m58s against a 15 minute CI timeout, so there is limited headroom.
- The new workflows (`security`, `codeql`, `images`, `system-tests`, `k6-smoke`), the README badges and Dependabot cannot be verified until the owner pushes.
