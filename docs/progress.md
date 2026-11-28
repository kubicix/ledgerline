# Ledgerline progress and agent handoff

Updated: 2026-11-28. Repository: https://github.com/kubicix/ledgerline (public).

## Checkpoint

- M1: complete and merged through [PR #1](https://github.com/kubicix/ledgerline/pull/1).
- M2: complete and merged through [PR #2](https://github.com/kubicix/ledgerline/pull/2).
- M3: complete and merged through [PR #3](https://github.com/kubicix/ledgerline/pull/3)
  (merge commit 142f201, BASE 1e19932).
- M4: complete and merged through [PR #30](https://github.com/kubicix/ledgerline/pull/30)
  (merge commit bff7de0, BASE d0bb564).
- M5 part A (M5-T01 to M5-T06, Kafka event stream and MongoDB read model): complete
  and merged through [PR #31](https://github.com/kubicix/ledgerline/pull/31) (merge
  commit df38ea7, BASE f8b8faf).
- M5 part B (M5-T07 to M5-T11, reconciliation and Phase 2 deployment): complete on
  branch `milestone-5b` (BASE df38ea7).
- M6: not started.
- Overall: 76 of 82 planned tasks, approximately 93%. Counting basis: `docs/plan.md`
  has 83 Done boxes, including the optional M6-T07 Angular admin panel; 82 excludes
  it; 76 boxes are ticked. Tasks count equally; this is not an effort or
  production-readiness estimate.

## Checklist

- [x] M1: messaging, event-sourced double-entry ledger and initial payment flows.
- [x] M2 T01-T05: JWT, Redis idempotency and Risk.
- [x] M2 T06-T07: bank operations, failures and settlements.
- [x] M2 T08-T11: pure sagas, holds, refunds, status commands and trial balance.
- [x] M2 T12-T15: full top-up, merchant payment and refund runtime.
- [x] M2 T16-T19: Notifications, publication-aware timeouts, Gateway and full Compose.
- [x] One fresh whole-branch review and regression-tested Important fixes.
- Integration record: [PR #2 checks and merge](https://github.com/kubicix/ledgerline/pull/2).
- [x] M3: tracing, metrics, failure/load verification and CI expansion.
- Integration record for M3: [PR #3 checks and merge](https://github.com/kubicix/ledgerline/pull/3).
  Hosted results are under Verification evidence.
- [x] M4: Kubernetes with Helm on kind, Kubernetes-friendly readiness and shutdown,
  pod-deletion and rolling-restart resilience, CI jobs `kind` and `compose-validate`.
- Integration record for M4: [PR #30 checks and merge](https://github.com/kubicix/ledgerline/pull/30).
  Hosted results are under Verification evidence.
- [x] M5 part A: Kafka event stream fed through the outbox (second transport, one relay
  loop per transport, switch `Messaging:Kafka:Enabled` off by default) and the
  Reporting read model in MongoDB (idempotent projections, rebuild from offset zero,
  read API). Decisions: [M5 decisions](m5-decisions.md).
- [x] M5 part B: end-of-day reconciliation (pure three-way matcher, Kafka staging,
  Quartz job on a clustered ADO.NET store, Redis lock, run lifecycle, outbox events,
  admin API, Payments `reconciled_at`, operations mail) and the Phase 2 deployment
  (Dockerfiles, Compose profile `phase2` with `compose.phase2.yaml`, gateway routes,
  Helm `phase2.enabled`, `Phase2SystemTests`, README section).
- [ ] M6: Blazor UI.

## Current technology

.NET 10, ASP.NET Core Minimal APIs, EF Core 10, PostgreSQL, RabbitMQ with a
transactional outbox/inbox, Redis, Keycloak JWT/OIDC, YARP, MailKit SMTP and Mailpit.
Tests use xUnit v3, Shouldly and Testcontainers. Docker Compose starts the complete
system. M3 adds OpenTelemetry (traces and metrics to the otel-lgtm container with
Tempo, Prometheus and Grafana), k6 (`grafana/k6:2.4.0`), gitleaks, CodeQL and Trivy.
M4 adds kind v0.33.0 (node `kindest/node:v1.37.0`), Helm v4.3.2 with one umbrella
chart at `deploy/helm/ledgerline` and dev-grade infrastructure templates, and the
`Microsoft.Extensions.Diagnostics.HealthChecks` broker check.
M5 part A adds Kafka (`apache/kafka`, KRaft single node) and MongoDB in the Compose
profile `phase2`, `Confluent.Kafka` (outbox publisher in
`Ledgerline.BuildingBlocks.Messaging.Kafka`, consumer in Reporting), `MongoDB.Driver`,
and the Testcontainers Kafka and MongoDb modules.
M5 part B adds Quartz.NET 4.7.3 (clustered ADO.NET job store on `reconciliation_db`)
and the Reconciliation service; the Helm chart gains Kafka and MongoDB templates behind
`phase2.enabled`.
Exact verified versions are in ADR 0000. Later-milestone technologies are planned,
not implemented by M5.

## Verification evidence

M5 part B, local (2026-11-28, Windows, Docker Desktop), branch `milestone-5b`:
- `dotnet build Ledgerline.slnx -c Release`: 0 warnings, 0 errors (32 projects).
- Full suite `dotnet test --solution Ledgerline.slnx --max-parallel-test-modules 1`,
  with no Compose project and no kind cluster up: 736 total, 728 passed, 8 skipped,
  0 failed in 12m46s (566 at the end of part A, plus 170 new tests; the 8 skips are
  the opt-in system tests and the benchmark). The hosted `build-test` (7m37s on part
  A) should grow by about a minute and stay well inside its 15 minute timeout.
- System tests (`LEDGERLINE_SYSTEM_TESTS=1`, both stack shapes, the Phase 2 one on a
  fresh project with `-f compose.yaml -f compose.phase2.yaml --profile app --profile
  phase2`): 8 of 8 passed in 9m55s, including `Read_model_catches_up_and_matches_ledger_after_workload`
  and `Reconciliation_of_today_is_clean_after_workload_and_flags_a_tampered_row`.
  Estimated hosted `system-tests`: about 12 to 15 minutes against its 25 minute test step.
- Default path unchanged: `docker compose --profile app up --build -d --wait` and
  `scripts/smoke.sh` on a fresh named project printed `SMOKE OK` (trial balance 0);
  the two Phase 2 gateway routes answer 503 and readiness stays 200.
- `render_test.sh`: `RENDER OK`, asserting `phase2.enabled` false and true and
  `infraOnly=true` with Phase 2; the chart's `files/` copies are byte-identical to
  their sources.
- kind (Task 11, on fresh clusters): default `kind-deploy` plus `smoke.sh
  http://localhost:8080` printed `SMOKE OK` with 12 pods; `kind-deploy --set
  phase2.enabled=true` as a first install gave 16 of 16 pods Ready, `SMOKE OK`, and a
  Reporting statement and `GET /api/reconciliation/runs` answered 200 through the
  gateway (node memory 5.1 of 15.5 GiB).
- Trivy 0.70.0 on `ledgerline-reporting:local` and `ledgerline-reconciliation:local`:
  clean for HIGH and CRITICAL with a fix available. gitleaks: no leaks.
  `dotnet list package --vulnerable --include-transitive`: none.
- Reviews: per-task reviews, one whole-branch Opus review (no Critical; its Important
  findings fixed in `4b335c6`) and one Fable audit of the matcher and the run (no
  Critical; its two Important findings fixed in `08503c9` and re-reviewed). Rulings,
  deviations, deferred minors and owner items: [M5 decisions](m5-decisions.md).
- Not yet verified: the hosted checks on the pull request and on `main` after the merge
  (`images` now builds eight images; `system-tests` now runs two stack shapes).

M5 part A, local (2026-11-28, Windows, Docker Desktop):
- `dotnet build Ledgerline.slnx -c Release`: 0 warnings, 0 errors.
- Full suite `dotnet test --solution Ledgerline.slnx --max-parallel-test-modules 1`:
  566 total, 560 passed, 6 skipped, 0 failed in 11m47s (530 at the end of M4,
  plus 36 new tests). A first run failed the M1 test `RoundTripTests.Ping_from_one_service_produces_pong_from_the_other` on an at-least-once wire duplicate; the test was fixed (`fc8b109`) and the rerun was green. The local run grew by about 1m20s over M4, so the hosted `build-test` (8 minutes at the end of M4) should stay near 9 to 10 minutes, inside its 15 minute timeout. The 6 skips are the opt-in system tests and the benchmark.
- System tests (`LEDGERLINE_SYSTEM_TESTS=1`): 6 of 6 passed in 6m05s.
- `docker compose --profile phase2 up -d --wait kafka mongo` plus `kafka-init` on a
  fresh project: the three event topics have 6 partitions and `retention.ms=-1`, and
  `ledgerline.reporting.dead.v1` exists with 1 partition.
- Default path unchanged: `docker compose config --quiet` passes; `--profile app up
  --build` with Kafka disabled and `scripts/smoke.sh` printed `SMOKE OK`;
  `render_test.sh` printed `RENDER OK` on a clean export of HEAD (a working copy with
  `core.autocrlf=true` can hold a CRLF copy of the chart's `init-databases.sql`, which
  makes the byte comparison fail locally).
- gitleaks: no leaks. `dotnet list package --vulnerable --include-transitive`: none.

M4, hosted CI:
- Pull request #30: `build-test` (7m22s), `security`, `compose-validate`, `analyze`
  and CodeQL passed. The GitGuardian check flagged the development Keycloak password
  that `render_test.sh` asserts (the same value as in `compose.yaml`); the owner
  closed the incident.
- Merge commit bff7de0 on main: `kind` (deploy, smoke and resilience, 8 minutes),
  `build-test`, `system-tests`, `images`, `security` and `compose-validate` passed.
  The last M4 checklist row ("CI deploys to kind") is ticked on this evidence.

M4, local, on branch `milestone-4` before the merge:
- Full suite once at the end, with no kind cluster and no Compose project up: 530
  total (525 plus the five new HealthTests), 524 passed, 6 skipped (the same six as
  M3), 0 failed, 10m27s.
- Release solution build: 0 warnings, 0 errors. `render_test.sh` prints `RENDER OK`.
- Fresh cluster: `kind-up` and `kind-deploy` (two passes) then `smoke.sh
  http://localhost:8080` printed `SMOKE OK` with all 12 pods Ready; a second
  `kind-deploy` (single upgrade) and `helm uninstall` followed by `kind-deploy` both
  succeeded (1032 s in total).
- `kind-resilience.sh` printed `RESILIENCE OK` on three consecutive runs, twice: on
  the Task 7 commit (689, 663 and 651 s) and again, on a fresh cluster, after the
  audit fix that makes its checks fail closed (659, 721 and 496 s). Every run: no
  payment Processing, ManualReview 0, trial balance 0, no failed gateway request in
  the rolling restart (7 to 26 s rollouts with two Payments replicas).
- Compose smoke on a fresh named project printed `SMOKE OK`; the project was brought
  down without `-v`.
- gitleaks clean; no vulnerable packages; the diff checks of the handoff passed.
- Whole-branch review: no Critical or Important findings; the recommended Minor fixes
  are in 347a531. Final audit of the shutdown and resilience changes: clean after
  4bb03ff. Rulings and deferred findings: [M4 decisions](m4-decisions.md).

M3:
- Baseline full suite before M3: 504 of 505 passed, 11m19s. The one failure was a
  flake; the focused rerun was green (66 of 66).
- Final full suite: 525 total, 519 passed, 6 skipped, 0 failed, 10m58s, without the
  `--ignore-exit-code 8` zero-test exemption.
- Release solution build: 0 warnings, 0 errors.
- FailureSystemTests: 5 consecutive runs passed, 3 of 3 each (3m41s to 4m53s), after the harness kill-race fix 92c3595. Earlier batches on 6989667 gave 3 of 5 and 4 of 5; every failure was the harness restart race, no money assertion failed.
- ReplaySystemTests and SmokeSystemTests pass.
- k6: smoke passed in 1 of 4 runs and every other target missed and is published as
  a miss (host disk fsync stalls, serial relay confirms, k6 sharing the CPU); the
  broker-outage acceptance was 1500/1500; trial balance 0 and ManualReview 0 after
  every run. See [k6 results](results/k6-2026-11-26.md).
- gitleaks 8.30.1 clean (git history and worktree, positive control flagged);
  Trivy 0.70.0 clean for HIGH and CRITICAL with a fix available on all 6 service
  images; no vulnerable packages across 25 projects.
- Hosted CI on the merge commit 142f201 (push to main): `build-test`, `security`,
  `images` and `system-tests` passed.
- Hosted CI, red while the repository was private: `codeql` failed at the analyze
  step because code scanning was not enabled, and on pull requests the `security`
  job failed at its last step, dependency review, with `Forbidden` for the same
  reason; the scans before it ran. The repository was made public on 2026-11-26
  after a full-history gitleaks scan with no findings. Whether both jobs are green
  now is not verified until the next run on main and on a pull request.
- Hosted CI, not yet verified: `k6-smoke` runs only on the nightly schedule
  (02:17 UTC) and no scheduled run has happened yet. README badges were not checked.
- Dependabot is active: it opened pull requests #4 to #20 on 2026-11-26. Sixteen
  are major bumps (.NET 11 images, `Microsoft.Extensions.*` 11, Npgsql 11) that
  conflict with the net10.0 and 10.0.x rule. #20 (Npgsql EF Core provider 10.0.4)
  is the only one on the 10.0 line, and its `build-test` is red. None is merged.

```powershell
dotnet build Ledgerline.slnx -c Release
dotnet test --solution Ledgerline.slnx --max-parallel-test-modules 1
docker compose --profile app up --build -d --wait
./scripts/smoke.ps1
```

Use a fresh named Compose project when proving a new milestone's complete flow,
so stable idempotency keys do not merely replay earlier demo operations. Stop only
the intended project; preserve existing volumes. The proof projects were stopped
without deleting their data.

## Continuation instructions

1. Open owner decisions before or alongside M6: the items in
   [M5 decisions](m5-decisions.md) (part A and part B open owner items, among them the
   reconciliation refund-row gap, the Compose Kafka log directory and `contactEmail`
   on Kafka), [M4 decisions](m4-decisions.md) (including binding kind's host ports to
   `127.0.0.1`) and [M3 decisions](m3-decisions.md) (Dependabot major bumps, nightly
   `k6-smoke`, LICENSE, secret scanning).
2. Then start at M6-T01 with an M6 handoff, from updated main on a new branch. Read the
   next task's spec in [the plan](plan.md). `kind`, `helm` and `kubectl` are installed.
   Phase 2 needs a PostgreSQL data directory created by the current
   `init-databases.sql` (fresh Compose project or fresh kind cluster); see the README.
3. Use one fresh named Compose project at a time and stop only that project. Never
   run `down -v`; preserve existing volumes. The system-test harness uses its own
   `ledgerline-systest` project and refuses another Ledgerline project, so stop the
   default one before running system tests.
4. Avoid building shared output DLLs while test processes use them on Windows. Run
   full integration modules serially (`--max-parallel-test-modules 1`). The full
   suite takes about 13 minutes; run focused tests unless the full suite is required.
   The system tests run two stack shapes one after the other (`ledgerline-systest`
   and `ledgerline-systest-phase2`); a same-day local rerun of `Phase2SystemTests`
   needs `LEDGERLINE_SYSTEM_TESTS_PHASE2_PROJECT=<fresh name>`.
5. The user-owned `orchestrator-prompt.md` and `docs/plan-M3-HANDOFF.md` are tracked
   on main since 3564d0a; do not edit or delete them. The `.superpowers/` ledger
   stays out of every commit. Stage files by name.
6. Do not repeat completed tasks or dispatch another M3, M4 or M5 reviewer.
   Deferred items are in [M3 decisions](m3-decisions.md),
   [M4 decisions](m4-decisions.md) and [M5 decisions](m5-decisions.md).
7. The kind cluster binds host ports 8080, 8081, 3000 and 8025, the same as Compose;
   run one of them at a time. `scripts/kind-down.*` deletes only the `ledgerline`
   cluster.

Review and inherited minor findings: [M2 review](m2-review.md).
All execution rulings and deferred costs: [M2 decisions](m2-decisions.md),
[M3 decisions](m3-decisions.md), [M4 decisions](m4-decisions.md) and
[M5 decisions](m5-decisions.md).
Full scope and remaining tasks: [implementation plan](plan.md).
