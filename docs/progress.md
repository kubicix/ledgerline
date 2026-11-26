# Ledgerline progress and agent handoff

Updated: 2026-11-26. Repository: https://github.com/kubicix/ledgerline (public).

## Checkpoint

- M1: complete and merged through [PR #1](https://github.com/kubicix/ledgerline/pull/1).
- M2: complete and merged through [PR #2](https://github.com/kubicix/ledgerline/pull/2).
- M3: complete and merged through [PR #3](https://github.com/kubicix/ledgerline/pull/3)
  (merge commit 142f201, BASE 1e19932).
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
- Integration record for M3: [PR #3 checks and merge](https://github.com/kubicix/ledgerline/pull/3).
  Hosted results are under Verification evidence.
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

1. Open owner decisions before or alongside M4 (see also the open items in
   [M3 decisions](m3-decisions.md)): confirm that `codeql` and dependency review
   pass now that the repository is public; close the major Dependabot pull requests
   and add a major-version ignore to `.github/dependabot.yml`, or accept them; check
   the first nightly `k6-smoke` result; decide on a LICENSE file and on enabling
   secret scanning with push protection.
2. Then start at M4-T01. Read [ADR 0011](adr/0011-kubernetes-packaging.md) and the
   next task's spec in [the plan](plan.md). Create the next branch from updated main.
   `kind` and `helm` are not installed on the development machine yet; `kubectl` is.
3. Use one fresh named Compose project at a time and stop only that project. Never
   run `down -v`; preserve existing volumes. The system-test harness uses its own
   `ledgerline-systest` project and refuses another Ledgerline project, so stop the
   default one before running system tests.
4. Avoid building shared output DLLs while test processes use them on Windows. Run
   full integration modules serially (`--max-parallel-test-modules 1`). The full
   suite takes about 11 minutes; run focused tests unless the full suite is required.
5. The user-owned `orchestrator-prompt.md` and `docs/plan-M3-HANDOFF.md` are tracked
   on main since 3564d0a; do not edit or delete them. The `.superpowers/` ledger
   stays out of every commit. Stage files by name.
6. Do not repeat completed tasks or dispatch another M3 reviewer. Deferred items are
   in [M3 decisions](m3-decisions.md).

Review and inherited minor findings: [M2 review](m2-review.md).
All execution rulings and deferred costs: [M2 decisions](m2-decisions.md) and
[M3 decisions](m3-decisions.md).
Full scope and remaining tasks: [implementation plan](plan.md).
