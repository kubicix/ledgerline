# M2 final review checkpoint

Reviewed range: `e5843e9..7e09b7a`, all 19 M2 implementation commits.
Reviewer: fresh-context GPT-6 Astra, read-only, one whole-branch review.

The implementation checkpoint passed 491/491 tests, a warning-free solution build
and fresh Compose smoke through real Keycloak and Gateway. Review then identified
seven Important recovery/concurrency issues. A targeted runtime check established
an eighth issue in Gateway timeout classification. No Critical or Minor findings
were reported. All findings and the recovered-cache counter continuation now pass
their regressions and the full 505/505 solution suite, with zero failures/skips.
The Release build has zero warnings/errors. Compose and upgrade proofs below also
passed. [PR #2](https://github.com/kubicix/ledgerline/pull/2) records the authoritative
GitHub checks and merge state.

## Findings and current evidence

| Finding | Current state |
|---|---|
| Reordered account status events overwrite newer state | Version guard passes reversed delivery from two replicas |
| Redis fallback velocity races across currencies and replicas | Customer-wide transaction lock passes; durable counter floor also preserves history after Redis recovery |
| Retention removes command evidence and stalls all due timeouts | Regression failed before the missing-row handling; now passes for both affected and unrelated payments |
| An old freeze retry can undo a newer unfreeze | Durable 204/no-op replay and cross-account hash tests pass |
| A refund stays Processing after consumer retries exhaust | Durable resend recovers after five failures while pending capacity remains reserved |
| Abandoned Redis leases block retries for 24 hours | Bounded 30-second leases and unique ownership; before/after database commit and successor-owner tests pass |
| Database velocity mixes requestedAt with evaluatedAt | Persisted request time passes delayed-delivery and midnight-boundary regression |
| Gateway deadline returns 503 instead of 504 | Stalled Kestrel upstream reproduced 503; timeout-feature classification now passes a full 504 problem response test |

Primary locations are `Features/AccountReplica/AccountStatusChangedHandler.cs`,
Risk's `Features/Evaluate/{EvaluateRiskHandler,DatabaseVelocityCounter}.cs`,
`Sagas/SagaTimeoutPoller.cs`, Accounts' `Features/Accounts/FreezeEndpoint.cs`,
`Features/Refunds/{CreateRefundEndpoint,RefundSaga}.cs`, Web's
`Idempotency/RedisIdempotencyStore.cs`, and Gateway's `Program.cs`.

The targeted tests above passed after their failures were observed. The whole
solution then passed 497/497 tests, with zero failures/skips, in 8m12s after these
independent fixes. The solution build had zero warnings/errors. A further complete
suite subsequently passed 505/505 after the approved schema-dependent fixes.
The one fresh review and its regression-tested fix pass are complete; no second
independent review was dispatched.

## Approved correction package

The user explicitly approved all four changes and their migrations on 2026-11-13,
including the earlier account status Version question. No new external dependencies
were added. The following describes the implemented correction package.

1. Add the existing Accounts stream version to `AccountStatusChanged`; persist
   `account_refs.status_version` and apply only newer events. An obsolete event
   must be acknowledged, while a missing account still retries.
2. Add `account_status_requests`, keyed by caller and idempotency key, with target
   account, request hash and creation time. Record successful no-ops too. Replay
   204 without applying the action again, and reject a different path/body with
   422 during Redis outages as well. Coordinate key scope with account creation.
3. Persist `risk_decisions.requested_at`. Use a consistent request timestamp for
   Redis and database velocity, and for independently rebuilding requested-day
   totals. Clarify the specification's conflicting evaluation/request time rule.
   Existing rows can only be backfilled from their known evaluation timestamp.
4. Add nullable `saga_timeouts.refund_id` and reuse the publication-aware recovery
   mechanism for child refunds. Resend the same refund operation ID after lost
   commands/replies. An unknown result must keep its pending capacity reserved;
   never mark it definitively failed merely because transport retries exhausted.

Verified regressions include reversed/concurrent status delivery, freeze replay
after an intervening unfreeze, cross-account key reuse with Redis unavailable,
delayed Risk requests spanning UTC midnight, and refund recovery after five
consumer failures and subsequent recovery. Preserve parent-payment-first locking.

## Accepted scope boundaries

M3 telemetry/load/security expansion and system failure suites, later reporting and
reconciliation, Kubernetes, production identity hardening and real-acquirer
compatibility remain outside M2. SMTP is at-least-once; Gateway rate limits are
per instance. These decisions and their costs are in [M2 decisions](m2-decisions.md).

Final integration proof: fresh full Compose startup and both smoke scripts passed.
Mailpit now uses persistent storage, and all seven message identities survived a
restart. The legacy demo database also migrated successfully: all three Risk
timestamps were backfilled, trial balance stayed zero, and capture/refund totals
remained 2000/500. Require green PR checks before integration. Stop at M2; do not
start M3 without a new user instruction.

## Deferred M2 minor finding

The local Linux migration image emits a missing optional `libgssapi_krb5.so.2`
diagnostic. Password-authenticated migrations exited successfully and the data
checks above passed. Review native GSS packaging if later deployment authentication
needs it; no unrelated authentication or image dependency change was made.

## Inherited M1 minor findings

- Broaden amount-edge theories and complete problem-shape assertions across all POSTs.
- Remove incidental trailing blank lines at end of files when a later scoped edit permits it.

These are carried from M1, not newly reported M2 findings. Do not expand the M2
Important-finding fix pass to incidental cleanup.
