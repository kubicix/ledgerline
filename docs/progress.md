# Ledgerline implementation progress

Updated: 2026-11-14. Repository: https://github.com/kubicix/ledgerline.

## Current checkpoint

- M1: complete, reviewed and merged through PR #1.
- M2: all 19 implementation tasks verified. The four schema-dependent corrections
  were explicitly approved and implemented. All 505 regression tests pass;
  Compose and GitHub integration gates are in progress.
- M3 and subsequent milestones: not started.
- Overall completed tasks: 44 of 82, approximately 54%. These percentages count tasks equally;
  they do not estimate effort or production readiness.

The user authorized completing M2, final review, the PR and its merge, then
pausing. Do not start M3 without a new instruction.

## Milestone checklist

- [x] M1: foundations, messaging, ledger and initial payment flows.
- [x] M2: full payment behavior and service integration implemented and locally verified.
  - [x] T01-T05: JWT, Redis idempotency and Risk.
  - [x] T06-T07: BankSimulator operations, failures and settlements.
  - [x] T08-T11: pure sagas, holds, refunds, account status and trial balance.
  - [x] T12-T15: bank client, full top-up, merchant payments and refund runtime.
  - [x] T16: Notifications and SMTP delivery.
  - [x] T17: publication-aware saga timeouts.
  - [x] T18: Gateway.
  - [x] T19: Compose, architecture and end-to-end verification.
  - [x] Final fresh review.
  - [x] All review fixes and final regression suite.
  - [ ] PR checks and merge.
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
   tasks at this checkpoint cover all M2 implementation work. The review checkpoint
   and integration PR are recorded below when available.
4. During active execution, `.superpowers/sdd/plan/progress.md` contains task
   evidence and rulings. Trust committed completion records over remembered
   context. Do not redo completed tasks.
   Read [the final review checkpoint](m2-review.md) before continuing. The user approved
   the entire migration/API package; do not ask for that authorization again.
   Require final green regression/CI gates before merging.
5. Run tests before claiming success. Avoid building projects while another test
   process is using their output DLLs on Windows. Run full-solution integration
   modules serially to avoid overloading RabbitMQ during vhost startup.
6. Do not touch the user-owned untracked `orchestrator-prompt.md` in the original
   checkout. Never include secrets or local credentials in this document.

## Verified gates

Final review-fix gate: 505/505 solution tests passed, zero failures/skips, in
8m42s. The Release solution build passed with zero warnings/errors. This includes
all approved migrations, durable admin/no-op replay, monotonic status events,
refund recovery after exhausted deliveries, delayed Risk timestamps and preserving
velocity history when Redis recovers. All Important review findings are closed.

Final implementation gate: all 491 solution tests passed, with zero failures or
skips. The solution build passed with zero warnings and errors. Tests ran serially
by module with the command below. Final review fixes, if required, need a new suite gate.

Independent review-fix checkpoint: 497/497 solution tests passed, zero failures or
skips, in 8m12s. Customer-wide Risk serialization, retained-command timeout
recovery, bounded/owned Redis leases and Gateway 504 classification have verified
regressions. The four schema/API-dependent findings were subsequently approved and
their full regressions now pass in the 505-test gate above. M2 has not
been pushed, and its PR/merge have not been performed. See [review details](m2-review.md).

After T15-T16: Payments integration 60/60, Payments pure tests 159/159, contract
samples 22/22, Notifications integration 10/10, Notifications templates 9/9,
and solution build with zero warnings and errors. These are
subsystem results, not the final M2 whole-solution verification.

T17 TimeoutTests passed 7/7, including one and three top-ups accepted during a
broker outage. T18 Gateway tests passed 18/18. T19 architecture guards passed
5/5. A fresh `ledgerline-m2-proof` Compose project passed both PowerShell and
Bash smoke scripts through Gateway and real Keycloak: Alice 6000, Bob 2500,
merchant 1500, all currency trial balances zero and at least four Mailpit emails.
The task's containers were stopped without deleting their volumes afterward.

Useful verification commands:

```powershell
dotnet build Ledgerline.slnx
dotnet test --solution Ledgerline.slnx --max-parallel-test-modules 1
```

The final M2 checkpoint will update this document with the reviewed commit,
PR/merge links, final evidence, accepted decisions and deferred findings.
Execution decisions and their tradeoffs are preserved in [M2 decisions](m2-decisions.md).

After M2 integration and a new user instruction to start M3, the next task is M3-T01, distributed
tracing across HTTP, the outbox and RabbitMQ. Read ADR 0009 and the task brief
before adding packages; instrumentation versions must be verified individually.
