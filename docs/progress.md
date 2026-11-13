# Ledgerline progress and agent handoff

Updated: 2026-11-14. Repository: https://github.com/kubicix/ledgerline.

## Checkpoint

- M1: complete and merged through [PR #1](https://github.com/kubicix/ledgerline/pull/1).
- M2: all 19 implementation tasks and the final review corrections are verified.
  [PR #2](https://github.com/kubicix/ledgerline/pull/2) is the authoritative record
  of CI, security checks and merge state.
- M3 and later milestones: not started.
- Overall: 44 of 82 planned tasks, approximately 54%. This counts tasks equally;
  it is not an effort or production-readiness estimate.

The user requested stopping after M2 review, PR checks and merge. Do not begin M3
without a new instruction. Before resuming, verify that PR #2 is merged and its
checks passed. The user explicitly approved all four review migrations/API changes;
that approval is recorded in the chat and execution ledger and must not be requested again.

## Checklist

- [x] M1: messaging, event-sourced double-entry ledger and initial payment flows.
- [x] M2 T01-T05: JWT, Redis idempotency and Risk.
- [x] M2 T06-T07: bank operations, failures and settlements.
- [x] M2 T08-T11: pure sagas, holds, refunds, status commands and trial balance.
- [x] M2 T12-T15: full top-up, merchant payment and refund runtime.
- [x] M2 T16-T19: Notifications, publication-aware timeouts, Gateway and full Compose.
- [x] One fresh whole-branch review and regression-tested Important fixes.
- Integration record: [PR #2 checks and merge](https://github.com/kubicix/ledgerline/pull/2).
- [ ] M3: tracing, metrics, failure/load verification and CI expansion.
- [ ] M4: Kubernetes/Helm.
- [ ] M5: reporting, event streaming and reconciliation.
- [ ] M6: Blazor UI.

## Current technology

.NET 10, ASP.NET Core Minimal APIs, EF Core 10, PostgreSQL, RabbitMQ with a
transactional outbox/inbox, Redis, Keycloak JWT/OIDC, YARP, MailKit SMTP and Mailpit.
Tests use xUnit v3, Shouldly and Testcontainers. Docker Compose starts the complete
system. Exact verified versions are in ADR 0000. Later-milestone technologies are
planned, not implemented by M2.

## Verification evidence

- Final solution suite: 505/505 passed, zero failures/skips, 8m42s.
- Release solution build: zero warnings/errors.
- Fresh complete Compose startup passed. PowerShell and Bash smoke passed through
  Gateway and real Keycloak: Alice 6000, Bob 2500, merchant 1500, trial balance zero.
- One bank capture per payment; completed refund left no timeout behind.
- All seven Mailpit message identities survived restart; repeated smoke passed.
- Earlier demo databases migrated successfully. Three legacy Risk timestamps were
  backfilled; balances remained 1500/2500/6000; capture/refund totals stayed 2000/500.

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

1. Read [the plan](plan.md), the next task's spec and [review evidence](m2-review.md).
2. Inspect `git status` and branch before editing. The managed checkout directory
   still says `milestone-1`, while M2 work is on `codex/milestone-2`. Do not infer
   branch state from that directory name. The M2 base is `e5843e9`; final code fixes
   were committed as `c68eb5c` after independent fixes in `04117aa`.
3. On a new instruction to start M3, fetch main, confirm M2 integration, and create
   the next `codex/` branch from updated main. Start at M3-T01: distributed tracing.
   Read ADR 0009 and verify instrumentation package versions individually.
4. Avoid building shared output DLLs while test processes are using them on Windows.
   Run full integration modules serially locally to avoid overloading RabbitMQ
   during concurrent quorum/vhost startup.
5. Preserve the user-owned `orchestrator-prompt.md` in the original checkout and
   the externally managed worktree. The ignored execution ledger is retained.
6. Do not repeat completed tasks or dispatch another M2 reviewer. Important findings
   were closed in one fix pass; detailed decisions and costs are preserved below.

Review and inherited minor findings: [M2 review](m2-review.md).
All execution rulings and deferred costs: [M2 decisions](m2-decisions.md).
Full scope and remaining tasks: [implementation plan](plan.md).
