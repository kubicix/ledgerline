# Handoff: Ledgerline Milestone 3, BACKEND (execution prompt)

> Paste this whole file as the first message to the executing (orchestrator) agent. Attach:
> - `docs/plan.md` (the plan; Milestone 3 starts at the heading "# Milestone 3");
> - `docs/testing.md`, `docs/adr/0009-observability.md`, `docs/adr/0000-verified-versions.md`, `docs/specs/building-blocks.md`;
> - `docs/progress.md`, `docs/m2-review.md`, `docs/m2-decisions.md`.
>
> Tag the `superpowers:subagent-driven-development` skill. Execution runs **subagent-driven** (section 3).
> There is no CLAUDE.md or AGENTS.md in this repository. The house rules are the plan's Global Constraints plus this file.

---

You are the **orchestrator** (Claude **Opus 5.5**, effort medium) executing an approved implementation plan in Ledgerline, a .NET 10 payment and wallet backend built as microservices (GitHub: `kubicix/ledgerline`). Repo root: `C:\Users\kubicix\Desktop\microservices`. All build, test, Compose and script commands run from the repo root. Windows 11; PowerShell and Git Bash are both available; .NET SDK 10.0.402; Docker Desktop is required.

Nothing needs designing. The design, specs, ADRs and the plan were finished on 2026-11-13 and Milestones 1 and 2 were built from them. The owner asked for this handoff on 2026-11-25. **This file is the owner's explicit instruction to start Milestone 3.** `docs/progress.md` says "Do not begin M3 without a new instruction"; this is that instruction, so do not stop on that sentence.

Handing you this plan is the owner's approval of it. Everything a task names is pre-approved and needs no further confirmation, even where a general rule says to ask before adding a dependency or touching config: the packages it lists, its `compose.yaml` and CI workflow edits, `.gitleaks.toml`, `dependabot.yml`, and the removal of the zero-test exemption. Only what a task does **not** name is a stop-and-ask (section 7).

Your job:
1. Execute **M3-T01 to M3-T13** faithfully, in plan order, with a local commit after each task.
2. Verify everything (section 6).
3. Leave the per-task commits as they are. Do not squash (section 5).
4. Report (section 8).

Two precedence rules, both apply:
- **Behavior: the spec wins.** If code and `docs/design.md`, `docs/specs/*`, `docs/adr/*` or `docs/testing.md` disagree on behavior, the spec is right. Do not edit those documents to make code easier. If a spec is wrong, contradictory or impossible, stop and ask (section 7).
- **Names and paths: the codebase wins.** Where the plan names a type, namespace, helper, file or line that the code spells differently after M1 and M2, adapt to the code and record it as a ledger `Ruling:` line. Test names in the plan are the exception: they are binding and never change, because `docs/testing.md` and later tasks refer to them.

## 0. What this work is (so every subagent understands the why)

Ledgerline is a portfolio system that proves, with tests and measurements, that a message-driven payment backend never loses or duplicates money. M1 built the outbox, inbox and event-sourced double-entry ledger. M2 built sagas with compensation, Risk, a fake bank, Notifications, a YARP gateway and Keycloak JWT. Both are merged to `main`.

Milestone 3 adds no business features. It adds the evidence:
- one distributed trace from the HTTP request through the outbox and RabbitMQ to the last saga reply;
- custom metrics and a Grafana dashboard kept as code;
- system-level failure tests that kill real containers (broker outage, service kill, replay of every message);
- k6 load scenarios with published results, including the misses;
- CI security scans (dependency review, vulnerable packages, gitleaks, CodeQL, Trivy).

Decisions that are final. Do not reopen them, and reject review findings that try to:
- **Hand-rolled messaging.** No MassTransit, Wolverine, NServiceBus, MediatR, CAP. No Marten. No FluentAssertions. No Bitnami images or charts. Reason: ADR 0002; the project exists to show the mechanics.
- **OpenTelemetry to `grafana/otel-lgtm`**, not the Aspire dashboard. Reason: ADR 0009; dashboards must be versioned and screenshots reproducible.
- **Telemetry setup lives once**, in `Ledgerline.BuildingBlocks.Web` (`AddLedgerlineTelemetry(serviceName)`). The activity source is `Ledgerline.Messaging`; the meter is `Ledgerline`. Metric names are the ADR 0009 table, exactly.
- **Trace propagation design** is ADR 0009: `traceparent` is captured on the outbox row at enqueue (already implemented in M1-T04), the relay starts a PRODUCER span from it and writes `traceparent` to AMQP headers (header already written since M1-T06), the consumer starts a CONSUMER span as a child.
- **System tests are opt-in**: skipped unless `LEDGERLINE_SYSTEM_TESTS=1`, so `dotnet test --solution` stays fast. The local baseline benchmark is opt-in with `LEDGERLINE_BENCH=1`.
- **k6 targets in `docs/testing.md` section 5 are fixed before measurement.** A missed target is published as a miss with an explanation. Never tune a target or a threshold to make a run pass. A hot-account miss gets a section in ADR 0007 (shard or accept).
- **Accepted M2 limits stay as they are**: SMTP is at-least-once; gateway rate limits are per instance; Redis in-flight leases are 30 s; production Keycloak hardening and real-acquirer compatibility are non-goals.
- **Money is `long` minor units plus ISO 4217 code.** No `decimal`, `double` or `float` for amounts, including in k6 result processing code written in C#.

Out of scope. Do not act on, block on, or re-raise:
- Milestones 4 to 6 (Kubernetes/Helm, Kafka/MongoDB/reconciliation, Blazor UI).
- Inherited minor findings listed in `docs/m2-review.md` (amount-edge theories, trailing blank lines, the optional `libgssapi_krb5.so.2` diagnostic in the migration image). Fix one only if a task already edits that exact spot.

Migrations: **none in this run.** No task changes a schema. If you believe one is needed, that is a stop-and-ask.

## 1. Read first, in this order

1. `docs/plan.md`: read fully the header, **Global Constraints**, **Review Focus**, **Task Protocol**, **File Structure**, then the whole "# Milestone 3" section including the **Milestone 3 checklist**. After that, one task at a time.
2. `docs/adr/0009-observability.md` (whole), `docs/adr/0000-verified-versions.md` (whole, including its "not verified" list at the bottom).
3. `docs/testing.md`: section 3 (F1 to F5, exact test names), section 4 (CI job table), section 5 (k6 scenarios and target table).
4. `docs/specs/building-blocks.md`: section 9 (the Telemetry paragraph) and section 10 (the five invariants of the messaging layer).
5. `docs/progress.md` (continuation instructions), `docs/m2-review.md`, `docs/m2-decisions.md` (prior rulings; do not undo them).
6. Code to know before touching it:
   - T01, T02: `src/BuildingBlocks/Ledgerline.BuildingBlocks.Messaging/Outbox/OutboxRelay.cs`, `Outbox/EfOutbox.cs`, `RabbitMq/RabbitMqOutboxPublisher.cs`, `Consuming/ConsumerHost.cs`; `src/BuildingBlocks/Ledgerline.BuildingBlocks.Web/WebDefaults.cs`; every service and gateway `Program.cs`; `tests/Ledgerline.Testing/{InfrastructureFixture,BrokerProbe,ServiceHost}.cs`.
   - T02: `src/Services/Payments/Ledgerline.Payments/Sagas/SagaRunner.cs`; the Accounts `Ledger/Ledger.cs` and the trial balance feature.
   - T04: `tests/Ledgerline.ArchitectureTests/` (existing reflection guards).
   - T05 to T07: `tests/Ledgerline.SystemTests/StartupDeliveryTests.cs` (the only existing file there), `compose.yaml`, `scripts/smoke.ps1`, `scripts/smoke.sh`, the Bank Simulator fault-injection and settlement endpoints.
   - T11, T12: `.github/workflows/ci.yml`, `tests/Directory.Build.props`.

## 2. Current repo state (2026-11-25)

- Branch `main`, HEAD `1e19932` (`Merge pull request #2 from kubicix/codex/milestone-2`), identical to `origin/main`. Working tree clean.
- Untracked and owner-owned, never commit, never delete: `orchestrator-prompt.md`, and this file `docs/plan-M3-HANDOFF.md`.
- CI on `main` at `1e19932`: green (job `build-test`, about 9 to 10 minutes of a 15 minute timeout).
- Plan checkboxes: M1 25/25, M2 19/19, M3 0/13.
- Last recorded full suite: 505/505 passed, zero skips, 8m42s, on 2026-11-14. That is a record, not today's result. No known pre-existing failures. Your first action is to re-establish the baseline (section 3).
- What M3 already has in infrastructure: the `otel-lgtm` service in `compose.yaml` (image `grafana/otel-lgtm:0.41.0`, ports 3000, 4317, 4318, volume `lgtm-data`).
- What M1 and M2 already left in code (BCL `System.Diagnostics` only, nothing exported, verified by grep on 2026-11-25). T01 and T02 must build on these, not duplicate them:
  - `Consuming/ConsumerHost.cs`: `ActivitySource("Ledgerline.Messaging")`, a CONSUMER activity started from `envelope.TraceParent`, `Meter("Ledgerline")` with counters `ledgerline.inbox.duplicates`, `ledgerline.consumer.retries`, `ledgerline.consumer.dead_lettered`.
  - `Outbox/EfOutbox.cs` stores `Activity.Current?.Id` as `TraceParent`; `RabbitMq/RabbitMqOutboxPublisher.cs` writes the `traceparent` AMQP header. There is no PRODUCER span yet.
  - `Sagas/SagaRunner.cs`: `Meter("Ledgerline.Payments")` with counter `ledgerline.saga.manual_review`.
  - `Risk/Features/Evaluate/FallbackVelocityCounter.cs`: `Meter("Ledgerline.Risk")` with `ledgerline.risk.redis_fallback`.
  - `Web/Idempotency/RedisIdempotencyStore.cs`: `Meter("Ledgerline.Idempotency")` with `idempotency.redis.failures`.
- Not there yet: any OpenTelemetry package, no `Telemetry/` folder, no `deploy/observability/`, no `tests/k6/`, no `docs/results/`, no `scripts/k6.*`, no `codeql.yml`, no `dependabot.yml`, no `.gitleaks.toml`. Start at M3-T01, step 1.
- Other branches: `codex/milestone-2` and `milestone-1` are merged history. A stale Codex worktree exists at `C:/Users/kubicix/.codex/worktrees/milestone-1/microservices`. Never touch, merge, prune or delete any of them. `docs/progress.md` says to create a `codex/` branch; that wording is stale because this run uses Claude implementers.
- Docker right now: containers named `scorpii-*` belong to another project of the owner. Never stop, restart or remove them. Their host ports do not collide with Ledgerline's (5432, 5672, 15672, 6379, 8081, 3000, 4317, 4318, 8025, 1025 plus the app ports). No Ledgerline Compose project is running.

## 3. How to execute

- Engine: `superpowers:subagent-driven-development`. Per task: one fresh implementer, then a spec-compliance review, then a code-quality review, fixes until both pass, re-review, commit. Do not use brainstorming or writing-plans.
- Orchestrator does these itself, no subagent: branch creation, the baseline run, ticking the task's `- [ ] Done` box in `docs/plan.md`, commits, and re-running every Prove command.
- **Baseline before T01:** `dotnet build Ledgerline.slnx -c Release` (zero warnings) and `dotnet test --solution Ledgerline.slnx --max-parallel-test-modules 1`. Record the counts in the ledger. If anything is red, stop and ask (section 7).
- Never trust a subagent's report. After each task run its **Prove** command and `dotnet build Ledgerline.slnx` yourself and read the output.
- After T13: one whole-branch review (section 3.1) with the plan's Review Focus verbatim, the five invariants of `building-blocks.md` section 10, the Milestone 3 checklist, and every `Ruling:` line from the ledger.
- **Strictly serial, in plan order T01 to T13.** No parallel implementers. Reasons: T11 and T12 both edit `ci.yml`; T04 adds project references that change the solution build; T01 and T02 edit the same three messaging files; Windows locks shared output DLLs while a test process runs.

### 3.1 Models and effort

The Agent tool only sets effort when told to. This table is that instruction.

| Role | Model | Effort | Agent params |
|---|---|---|---|
| Orchestrator (you) | Opus 5.5 | medium | n/a |
| Implementers | see 3.2 (Claude only; never Fable) | see 3.2 | `model: "sonnet"` or `"opus"`, `effort` as listed |
| Spec and code-quality reviewers | Opus 5.5 | medium; `high` on tasks marked with a star | `model: "opus"` |
| Whole-branch reviewer | Opus 5.5 | medium | `model: "opus"`, `effort: "medium"` |
| Final critical audit | Fable 5.2, **once**, after the whole-branch Opus review is clean. Scope: the diff of the starred tasks (messaging pipeline changes in T01 and T02; the money assertions of T06 and T07) | medium | `model: "fable"`, `effort: "medium"` |
| Fixers | see 3.4 | see 3.4 | |

### 3.2 Implementer per task

Ledger and task tooling number these Task 1 to Task 13 (Task N is M3-T0N). A star marks tasks that touch the outbox, inbox, consumer pipeline or money invariants.

| Task | Implementer | Effort | Why |
|---|---|---|---|
| T01 OpenTelemetry wiring, trace across the broker ★ | opus | high | Edits `OutboxRelay`, `RabbitMqOutboxPublisher`, `ConsumerHost`; context propagation across retry and two hosts |
| T02 Custom metrics ★ | opus | medium | Same three files plus saga and ledger; gauges fed by background refresh |
| T03 Grafana dashboards as code | sonnet | high | JSON and a script, but provisioning paths and Prometheus metric names must be looked up, not guessed |
| T04 Failure test inventory | sonnet | medium | Reflection test plus any missing named test; missing tests may need opus if they are concurrency tests (escalate then) |
| T05 System test harness | opus | medium | Process control of Compose, token handling, invariants through the admin API |
| T06 System failure tests: broker outage, kills ★ | opus | high | Real `docker kill`, timing windows, must pass 5 times in a row |
| T07 System failure test: replay every message ★ | opus | high | Shovel re-publishing with same and new `messageId`; expected-value reasoning |
| T08 k6 library, smoke, steady | sonnet | high | Clear spec; k6 2.x API must be checked against the docs first |
| T09 k6 contention, mix, spike, outage, baseline | sonnet | high | Follows the T08 pattern; one C# benchmark test |
| T10 Results write-up | sonnet | medium | Docs from measured data; no invented numbers |
| T11 CI security scans | sonnet | medium | YAML and config; action versions must be verified |
| T12 CI image scan, system tests, k6 smoke | sonnet | medium | YAML plus removing the zero-test exemption |
| T13 README for M3 | sonnet | low | Docs with a self-checking grep |

Escalation: if a task is harder than rated (implementer reports confusion, or review finds a design-level problem), re-dispatch one tier up (sonnet to opus medium, opus medium to opus high) instead of iterating at the same tier. Ledger it as `Ruling: escalated ...`.

### 3.3 Dispatch mechanics

A subagent has no memory and sees only its brief. Every implementer brief contains:
- section 0 of this file, verbatim;
- the traps from section 4 that apply to the task, verbatim;
- the full task text from `docs/plan.md`, verbatim;
- the plan's Global Constraints and Task Protocol, verbatim;
- the exact spec sections the task names, verbatim, not summarized;
- the signatures and file paths produced by earlier M3 tasks it consumes (see 3.5), in their final state;
- repo root, and that commands run from the repo root;
- the footer: follow the Task Protocol (write the listed tests with the exact names, run Prove and see it fail, implement, run Prove and see it pass, `dotnet build Ledgerline.slnx` with zero warnings). **Do not commit, do not push, do not touch files outside the task's list without saying why, no schema changes, no new package the task does not name.** Report: files changed, last lines of the Prove output, build result, anything not done or uncertain. State in the brief that this footer **overrides** Task Protocol step 7 and the "Commit after every task" Global Constraint for the subagent: the orchestrator commits, the subagent never runs `git add` or `git commit`.

Every reviewer brief contains the task text, the relevant spec sections and the task's diff. Stage 1 spec compliance: every listed test exists with its exact name and asserts what the task says; Produces signatures match; nothing outside the file list changed without a stated reason; Global Constraints hold. Stage 2 code quality: correctness under concurrency and failure, transaction boundaries, no fixed delays in tests, no dead code, no speculative abstraction, style consistent with neighbors. Findings come back as a list with severity (blocker, should-fix, nit) and file and line.

### 3.4 Fix implementers and review findings

| Fix size | Model | Effort |
|---|---|---|
| Naming, formatting, analyzer warning, missing assertion, doc wording | haiku or sonnet | low to medium |
| Standard logic defect | sonnet high, or opus medium | |
| Same defect survived 2 fix rounds with opus | fable | medium or high, root cause plus fix; ledger `Ruling: escalated to fable ...` |

- A finding counts only if it cites the plan, a spec, an ADR, a decision in section 0, a Global Constraint, or a failing test. Findings that re-litigate section 0 are rejected and ledgered.
- Blockers and should-fix go to a fixer. Nits only if cheap.
- Maximum 5 fix rounds per task. A flaky test is a defect: find the cause, never add a retry or a sleep.

### 3.5 Files that span tasks

Pass the previous task's final state of these forward in the brief.

| File | Tasks |
|---|---|
| `Outbox/OutboxRelay.cs`, `RabbitMq/RabbitMqOutboxPublisher.cs`, `Consuming/ConsumerHost.cs` | T01, T02 |
| `Directory.Packages.props`, `docs/adr/0000-verified-versions.md` | T01, possibly T02, T11 |
| `compose.yaml` | T01, T03, T07 |
| `tests/Ledgerline.SystemTests/Harness/*` | T05, T06, T07 |
| `tests/k6/lib/*`, `scripts/k6.sh`, `scripts/k6.ps1` | T08, T09 |
| `.github/workflows/ci.yml` | T11, T12 |
| `README.md` | T10, T13 |
| `docs/plan.md` (Done boxes, M3 checklist) | every task |

## 4. Known traps

1. **Look up, do not recall.** The plan and ADR 0000 mark these as not verified. Check the primary source (nuget.org, the action README, the image docs, the k6 docs) before using them, then update the ADR 0000 table:
   - each OpenTelemetry package version. ADR 0009 says 1.21.0, but instrumentation packages version independently;
   - the Npgsql OpenTelemetry package that matches Npgsql 10.0.3;
   - the current status of the OpenTelemetry messaging semantic conventions (attribute names in T01);
   - the Grafana provisioning paths inside `grafana/otel-lgtm:0.41.0` (T03);
   - the k6 2.x script API for `http`, `scenarios`, `thresholds`, custom `Trend`, and that `grafana/k6:2.4.0` exists (T08);
   - the `github/codeql-action` major version, and whether `gitleaks/gitleaks-action@v3` needs a license for this repository type (T11);
   - the current gitleaks and Trivy image names and tags (T11, T12).
2. **Packages.** Every new package gets a `PackageVersion` in `Directory.Packages.props`. A package the plan does not name (for example a runtime-metrics instrumentation package to satisfy the "metrics (runtime, ...)" sentence of the spec) is a stop-and-ask, not a judgment call.
3. **Project reference rules still hold.** Gateway and BankSimulator reference neither `Contracts` nor `BuildingBlocks.Messaging`. Telemetry reaches them through `BuildingBlocks.Web` only. `ArchitectureTests` enforces this; do not weaken those tests.
4. **T01 and T02 change the heart of the system.** Their Prove is not only the new test class. Also run the whole `tests/Ledgerline.BuildingBlocks.Messaging.IntegrationTests` project and confirm the M1 failure tests (`FailureScenarioTests`, `OutboxRelayTests`, `ConsumerTests`, `RetryAndDeadLetterTests`) still pass. Spans and metrics must never change ack, commit or publish order (invariants 1 to 5 of `building-blocks.md` section 10).
5. **Meter names already diverge from the spec.** ADR 0009 and `building-blocks.md` section 9 define one meter, `Ledgerline`. The code has four: `Ledgerline` (ConsumerHost), `Ledgerline.Payments`, `Ledgerline.Risk`, `Ledgerline.Idempotency`. The spec wins on behavior: every ADR 0009 metric must be exported and visible in Prometheus. T02 moves the existing consumer counters into `Diagnostics/MessagingMetrics.cs` and the manual-review counter into `Sagas/SagaMetrics.cs` rather than creating second instruments with the same name. How `AddLedgerlineTelemetry` picks up the meters (rename to `Ledgerline`, or subscribe to `Ledgerline*`) is the implementer's choice inside T01 and T02; ledger it as a `Ruling:`. The Risk and Idempotency counters are not in the ADR table: keep them working and exported, do not delete them. Likewise T01 moves the existing `ActivitySource` into `Diagnostics/MessagingActivitySource.cs` instead of adding a second source with the same name.
6. **`docs/testing.md` asserts metrics in two existing tests**: `Same_message_delivered_twice_is_handled_once` ("duplicate metric is 1") and `Crash_after_publish_before_marking_processed_republishes_and_consumer_deduplicates` ("`inbox.duplicates` incremented"). The counter already exists in `ConsumerHost`, so check first whether those tests already assert it. If they do not, adding the assertions during T02 is allowed even though those files are not in the T02 list; ledger it as a `Ruling:`.
7. **Prometheus metric names are derived, not chosen.** T03 queries `ledgerline_outbox_lag_seconds_count`. The meter names of ADR 0009 are fixed; the Prometheus spelling depends on the OTLP translation in the LGTM image. Query the real series name from the running stack and make the dashboard and `scripts/check-dashboard.sh` use it. A difference from the plan's spelling is a names adaptation (`Ruling:`), not a reason to rename the instrument.
8. **Never log email addresses**, including in span attributes, metric tags, log scopes and k6 output. No em dashes in any tracked document or code comment. Docs and comments are English. Never describe the project as banking experience.
9. **`TreatWarningsAsErrors` is on.** A task is not done while `dotnet build Ledgerline.slnx` prints a warning.
10. **Windows build and test.** Do not build while a test process still holds shared output DLLs. Run the full suite serially: `dotnet test --solution Ledgerline.slnx --max-parallel-test-modules 1`. Parallel modules overload RabbitMQ during concurrent quorum queue and vhost startup. Filters are Microsoft.Testing.Platform options: `--filter-class`, `--filter-method`, `--filter-namespace`.
11. **Docker.** Re-check `docker ps` right before any Docker work. `compose.yaml` publishes fixed host ports, so only one Ledgerline Compose project can be up at a time: bring one down (`docker compose -p <name> --profile app down`, **never `-v`**) before starting another. Never delete volumes. Use a fresh named project for each proof (`-p ledgerline-m3-<purpose>`; the harness uses `-p ledgerline-systest`), because the smoke scripts use stable idempotency keys and a reused project only replays old operations. The Compose file sets `name: ledgerline`, so the default network is `ledgerline_default`; with `-p` the network name changes, and k6 `docker run --network ...` must follow it.
12. **Git Bash path mangling.** `docker run -v "$PWD:/repo"` breaks under Git Bash. Use `MSYS_NO_PATHCONV=1`, or run the command from PowerShell with `${PWD}`. Shell scripts are LF (`.gitattributes` has `*.sh text eol=lf`); new `.sh` files must be LF and have a PowerShell twin where the plan asks for one.
13. **`tests/Ledgerline.SystemTests` already has one test**, `StartupDeliveryTests`, with an assembly-level `InfrastructureFixture` (Testcontainers). It is not environment-gated and must keep running in the normal suite. Only the new Compose-based tests are gated by `LEDGERLINE_SYSTEM_TESTS=1`.
14. **T12 removes `--ignore-exit-code 8`** from `tests/Directory.Build.props`. After that a test project with zero discovered tests fails the suite. `tests/Ledgerline.Testing` is a library (`IsTestProject=false`), not a test project. Confirm every real test project still discovers at least one test with the gate variables unset.
15. **Long runs go to the background.** The T06 proof is 5 consecutive passes; k6 steady and mix scenarios are 3 to 5 minutes each plus setup. Run anything over about 2 minutes in the background and read its output; do not poll in a tight loop.
16. **CI budget.** `build-test` has a 15 minute timeout and already takes 9 to 10. T01 and T02 add integration tests. If the local full suite grows past about 11 minutes, ledger it and tell the owner in the final report; do not raise the timeout or drop tests on your own.
17. **The migration image prints a missing `libgssapi_krb5.so.2` diagnostic.** Known, harmless, deferred. Not a failure.
18. **EF Core.** No migration is planned. `dotnet ef database update` from the CLI is forbidden on any database. Services migrate through their own `migrate` command inside Compose jobs; that path is the only one used here.
19. **Task tooling parses `Task N`, not `M3-TNN`** (M2 ruling). Use Task 1 to 13 in the ledger and briefs; if a superpowers task script cannot parse the plan's `### M3-T01` headings, keep equivalent manual brief, BASE and completion bookkeeping.
20. **T10 needs facts only the machine and the owner have**: CPU model, cores, RAM, Docker resource limits, and two screenshots (`dashboard.png`, `trace-topup.png`). Read hardware from the machine. Take the screenshots with browser automation against `http://localhost:3000` if you have it; otherwise raise a `user_action` (section 5b). Never fabricate a number or a picture.

## 5. Git workflow for this run

There is no CLAUDE.md; these rules come from the plan's Global Constraints, the M1 and M2 history on `main`, and the owner's standing rule that nothing is pushed without asking.

- Branch: `git switch -c milestone-3 1e19932`. `git merge-base milestone-3 origin/main` must print `1e19932...`. That is BASE_SHA. Do not commit to `main`.
- **Commit after every task, with the exact message the plan gives** for that task (for example `feat(observability): trace requests across the outbox and broker`). Include the ticked `- [x] Done` box in that commit. Fixes found by later review go in follow-up commits (`fix: ...`), as M2 did.
- **No squash at the end.** This deviates from the usual squash default on purpose: the plan mandates per-task commits with fixed messages, and PR #1 and PR #2 were merged with their per-task history intact.
- No trailers. Recent `main` history has no `Co-Authored-By` lines; keep it that way.
- Stage exact files by name. Never `git add -A` or `git add .`: `orchestrator-prompt.md`, `docs/plan-M3-HANDOFF.md` and the `.superpowers/` ledger directory are untracked, are not in `.gitignore`, and must stay out of every commit. Do not edit `.gitignore` for them.
- Forbidden: `git push`, `reset --hard`, rebase, merge, `--amend`, `--no-verify`, branch deletion, touching the Codex worktree. The owner pushes and opens the PR.
- Final docs commit, after the whole-branch review is clean, following the M2 precedent (`ca33c21`): create `docs/m3-decisions.md` (every `Ruling:` with its cost if wrong, and deferred minors), tick the Milestone 3 checklist in `docs/plan.md`, update `docs/progress.md` (checkpoint, counts, verification evidence, continuation instructions pointing at M4-T01, and state the counting basis for the total: `docs/progress.md` says 82 planned tasks while `docs/plan.md` has 83 Done boxes, one of which is the optional M6-T07; count the boxes yourself and say which basis you used). Message: `docs: record M3 verification and agent handoff`. These three files plus the edits a task explicitly lists and ADR 0000 version updates are the only allowed documentation edits.

## 5b. Progress reporting (the plan-progress mod)

The owner's `plan-progress` mod draws the live band only from the ledger at `.superpowers/sdd/<plan>/progress.md` and the task briefs. Progress is shown there, not in chat. Do not write a manual checklist anywhere.

- Start each task with the superpowers `task-start` / `task-brief` calls; the band and the "approve after each task" pause watch those calls.
- Ledger line formats, exactly (the ledger is untracked scratch, so the document rules of the repo do not apply to it):
  - `Task N: implementer <model>/<effort>`
  - `Task N: complete (commits a..b, review clean)`
  - `Task N: Ruling: <what> — cost if wrong: <x>`
  - `Task N: fix round R/5 (<model>/<effort>: <finding>)`
  - `Final: review (...)`, `Final: fix wave (K)`, `Final: review clean`
- If a call is refused with `plan-progress: ... on hold`, end the turn and wait. One short line, no checklist.
- Anything the owner must do (start Docker Desktop, supply a screenshot, push the branch, answer a stop-and-ask) goes through `mcp__plan-progress__user_action` (load it with ToolSearch): `text` in Turkish with commands verbatim, and `task`. Send `clear: true` when the owner confirms ("Yaptım: ..."). Say it once in chat too.

## 6. Definition of done

Verified locally, all required:
1. M3-T01 to M3-T13 each committed with the plan's message; all 13 Done boxes ticked.
2. `dotnet build Ledgerline.slnx -c Release`: zero warnings, zero errors.
3. `dotnet test --solution Ledgerline.slnx --max-parallel-test-modules 1`: zero failures, **without** the `--ignore-exit-code 8` exemption; total count is the baseline plus the new tests, none removed.
4. Tests exist with these exact names and pass: the four `TracingTests`; the seven `MetricsTests` names of T02; `Every_documented_failure_test_exists`; `Stack_starts_and_smoke_flow_passes`; `Broker_outage_loses_no_money`; `Killing_payments_mid_saga_completes_after_restart`; `Killing_accounts_mid_posting_completes_after_restart`; `Replaying_every_message_changes_nothing`.
5. `FailureSystemTests` passed 5 times in a row with `LEDGERLINE_SYSTEM_TESTS=1`; `ReplaySystemTests` and `SmokeSystemTests` pass.
6. On a fresh named Compose project: smoke script passes; one top-up trace in Tempo has spans from gateway, payments, risk, accounts and notifications; `./scripts/check-dashboard.sh` exits 0.
7. `./scripts/k6.sh smoke` exits 0 with all thresholds passed. `transfer-hot-account`, `payment-mix` and `spike` ran to completion, with every missed threshold recorded. `LocalTransferBaseline` printed p50, p95 and transfers per second.
8. `docs/results/k6-<date>.md` exists with hardware, Docker limits, commit hash and one table per scenario; both images exist; `grep -c "to be measured" docs/when-monolith.md` prints 0; the README Measurements section has no `TBD`.
9. Local gitleaks run reports no leaks; `dotnet list Ledgerline.slnx package --vulnerable --include-transitive` reports none; local Trivy scan of the built service images exits 0 for HIGH and CRITICAL with a fix available.
10. The T13 README grep prints nothing (every test name in the README exists under `tests/`).
11. Diff checks over `git diff 1e19932..HEAD`: no forbidden dependency; no `decimal`, `double` or `float` for amounts; no em dash on added lines of tracked `.md` or `.cs` files; no new or changed EF migration; no edit to `docs/design.md`, `docs/specs/*` or `docs/testing.md`; no ADR edit other than ADR 0000 and, only if the hot-account target was missed, ADR 0007; no deleted file other than what T12 allows; `orchestrator-prompt.md`, this handoff and `.superpowers/` not committed.
12. Whole-branch Opus review clean, then the one Fable audit clean. `docs/m3-decisions.md` written, `docs/progress.md` updated, Milestone 3 checklist ticked.
13. Nothing pushed. No Compose project left running. No volume deleted. `scorpii-*` containers untouched.

Cannot be verified until the owner pushes. Report these as open, never as done:
- the new `security`, `codeql`, `images`, `system-tests` and `k6-smoke` workflows are green on GitHub;
- Dependabot and dependency review behave as configured;
- CI badges in the README resolve.

## 7. Stop and ask the user (do not guess) if

- The baseline build or full suite is red before T01.
- A needed package is not named by the task, or a named version does not restore and the nearest valid one is on a different major line.
- The gitleaks action needs a paid license for this repository, or the `codeql-action` major cannot be determined from its README.
- Adding spans or metrics would require changing ack, commit or publish ordering in `OutboxRelay`, `ConsumerHost` or `RabbitMqOutboxPublisher`.
- A system failure test exposes a real money defect (trial balance not zero, a wallet credited twice, bank captures not matching ledger entries). Stop with the evidence; do not patch business logic inside M3 on your own.
- `T04` reports a documented failure test as missing and writing it shows the behavior itself is absent, not just the test.
- A test would pass only by changing a contract, a message shape, a spec, a k6 target or a threshold.
- Any task seems to need a schema change or a migration.
- gitleaks finds a secret that is not one of the documented development passwords in `compose.yaml`, `deploy/compose/**` or the realm file. Never widen the allowlist beyond exact path and value.
- A Ledgerline port is taken by a container that is not yours, or Docker Desktop is down.
- T10 screenshots or hardware facts cannot be obtained by you.
- The same step fails twice after a genuine fix plus one escalation.
- Anything would need `main`, push, `reset --hard`, deletion of files, volumes or branches, or `dotnet ef database update`.

## 8. Final report to the user (Turkish, short)

- Sonuç: branch `milestone-3`, commit listesi (SHA + subject), BASE `1e19932`.
- Build sonucu; test sayıları (baseline ve bitiş); system testleri (5 ardışık koşu sonucu); k6 özeti (hedef, ölçülen, tutan ve tutmayan).
- Görev başına kullanılan model ve effort; escalation ve fallback'ler.
- Her `Ruling:` satırı, yanlışsa maliyetiyle. Değişen paket ve action versiyonları. Ertelenen minor bulgular.
- Lokal doğrulanan ve push sonrası doğrulanacak maddeler ayrı listelenir.
- Kullanıcının sırayla yapması gerekenler: branch'i push et, PR aç, yeni workflow'ların yeşil olduğunu kontrol et, gerekiyorsa screenshot ekle.
- "Hiçbir şey push edilmedi."
