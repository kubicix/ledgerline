# Ledgerline progress and agent handoff

Updated: 2026-11-26. Repository: https://github.com/kubicix/ledgerline.

## Checkpoint

- M1: complete and merged through [PR #1](https://github.com/kubicix/ledgerline/pull/1).
- M2: complete and merged through [PR #2](https://github.com/kubicix/ledgerline/pull/2).
- M3: all 13 tasks plus the whole-branch review and audit fixes are verified on
  branch `milestone-3` (BASE 1e19932). Nothing is pushed or merged; the owner
  pushes and opens the PR.
- M4 and later milestones: not started.
- Overall: 57 of 82 planned tasks, approximately 70%. Counting basis: `docs/plan.md`
  has 83 Done boxes, including the optional M6-T07 Angular admin panel; 82 excludes
  it; 57 boxes are ticked. Tasks count equally; this is not an effort or
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
- Integration record for M3: open until the owner pushes `milestone-3` and CI runs.
- [ ] M4: Kubernetes/Helm.
- [ ] M5: reporting, event streaming and reconciliation.
- [ ] M6: Blazor UI.

## Current technology

.NET 10, ASP.NET Core Minimal APIs, EF Core 10, PostgreSQL, RabbitMQ with a
transactional outbox/inbox, Redis, Keycloak JWT/OIDC, YARP, MailKit SMTP and Mailpit.
Tests use xUnit v3, Shouldly and Testcontainers. Docker Compose starts the complete
system. M3 adds OpenTelemetry (traces and metrics to the otel-lgtm container with
Tempo, Prometheus and Grafana), k6 (`grafana/k6:2.4.0`), gitleaks, CodeQL and Trivy.
Exact verified versions are in ADR 0000. Later-milestone technologies are planned,
not implemented by M3.

## Verification evidence

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
- Not verifiable until the push: the `security`, `codeql`, `images`, `system-tests`
  and `k6-smoke` workflows, README badges, Dependabot and dependency review.

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

1. The owner pushes `milestone-3`, opens the PR and checks CI first. Do not start
   M4 before that result is known (see the open items in [M3 decisions](m3-decisions.md);
   dependency review and CodeQL upload may be red on a private repository).
2. Then start at M4-T01. Read [ADR 0011](adr/0011-kubernetes-packaging.md) and the
   next task's spec in [the plan](plan.md). Create the next branch from updated main.
3. Use one fresh named Compose project at a time and stop only that project. Never
   run `down -v`; preserve existing volumes. The system-test harness uses its own
   `ledgerline-systest` project and refuses another Ledgerline project, so stop the
   default one before running system tests.
4. Avoid building shared output DLLs while test processes use them on Windows. Run
   full integration modules serially (`--max-parallel-test-modules 1`). The full
   suite takes about 11 minutes; run focused tests unless the full suite is required.
5. Preserve the user-owned `orchestrator-prompt.md` and `docs/plan-M3-HANDOFF.md`
   (untracked) and the ignored `.superpowers/` ledger. Stage files by name.
6. Do not repeat completed tasks or dispatch another M3 reviewer. Deferred items are
   in [M3 decisions](m3-decisions.md).

Review and inherited minor findings: [M2 review](m2-review.md).
All execution rulings and deferred costs: [M2 decisions](m2-decisions.md) and
[M3 decisions](m3-decisions.md).
Full scope and remaining tasks: [implementation plan](plan.md).
