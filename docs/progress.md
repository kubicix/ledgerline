# Ledgerline implementation progress

Updated: 2026-11-13. Repository: https://github.com/kubicix/ledgerline.

## Current checkpoint

- M1: complete, reviewed and merged through PR #1.
- M2: 15 of 19 tasks committed. Task 16, Notifications, is in progress.
- M3 and subsequent milestones: not started.
- Overall completed tasks: 40 of 82, approximately 49%. Completion of M2 will
  bring this to 44 of 82, approximately 54%. These percentages count tasks equally;
  they do not estimate effort or production readiness.

The user authorized completing M2, final review, the PR and its merge, then
pausing. Do not start M3 without a new instruction.

## Milestone checklist

- [x] M1: foundations, messaging, ledger and initial payment flows.
- [ ] M2: full payment behavior and service integration.
  - [x] T01-T05: JWT, Redis idempotency and Risk.
  - [x] T06-T07: BankSimulator operations, failures and settlements.
  - [x] T08-T11: pure sagas, holds, refunds, account status and trial balance.
  - [x] T12-T15: bank client, full top-up, merchant payments and refund runtime.
  - [ ] T16: Notifications and SMTP delivery.
  - [ ] T17: publication-aware saga timeouts.
  - [ ] T18: Gateway.
  - [ ] T19: Compose, architecture and end-to-end verification.
  - [ ] Final fresh review, required fixes, PR checks and merge.
- [ ] M3 and remaining plan tasks: see [the implementation plan](plan.md).

## Technology

.NET 10, ASP.NET Core Minimal APIs, EF Core 10, PostgreSQL, RabbitMQ with a
transactional outbox/inbox, Redis, Keycloak JWT/OIDC, Docker Compose, xUnit v3,
Shouldly and Testcontainers. M2 adds the YARP gateway and MailKit SMTP sender
with Mailpit locally. Exact verified versions are in ADR 0000.

## Continuation instructions for agents

1. Read `docs/plan.md` and the relevant specification under `docs/specs/`.
2. Verify the actual branch and `git status` before changing anything. Work is on
   `codex/milestone-2` in the existing managed worktree; its directory still has
   the historical `milestone-1` name.
3. The M2 base is `e5843e970f76e3bf1b88ddfbe985b74e1f8dfa55`. The last completed
   task at this checkpoint is T15, commit `70eb2cf`.
4. During active execution, `.superpowers/sdd/plan/progress.md` contains task
   evidence and rulings. Trust committed completion records over remembered
   context. Do not redo completed tasks.
5. Run tests before claiming success. Avoid building projects while another test
   process is using their output DLLs on Windows. Run full-solution integration
   modules serially to avoid overloading RabbitMQ during vhost startup.
6. Do not touch the user-owned untracked `orchestrator-prompt.md` in the original
   checkout. Never include secrets or local credentials in this document.

## Most recent verified gates

After T15: Payments integration 60/60, Payments pure tests 159/159, contract
samples 22/22, and solution build with zero warnings and errors. These are
subsystem results, not the final M2 whole-solution verification.

Useful verification commands:

```powershell
dotnet build Ledgerline.slnx
dotnet test --solution Ledgerline.slnx --max-parallel-test-modules 1
```

The final M2 checkpoint will update this document with the reviewed commit,
PR/merge links, final evidence, accepted decisions and deferred findings.
