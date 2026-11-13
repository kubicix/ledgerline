# M2 final review checkpoint

Reviewed range: `e5843e9..7e09b7a`, all 19 M2 implementation commits.
Reviewer: fresh-context GPT-6 Astra, read-only, one whole-branch review.

The implementation checkpoint passed 491/491 tests, a warning-free solution build
and fresh Compose smoke through real Keycloak and Gateway. Review then identified
seven Important recovery/concurrency issues. A targeted runtime check established
an eighth issue in Gateway timeout classification. No Critical or Minor findings
were reported. M2 is not ready to merge until the remaining findings are closed.

## Findings and current evidence

| Finding | Current state |
|---|---|
| Reordered account status events overwrite newer state | Awaiting approval for message Version and replica status_version |
| Redis fallback velocity races across currencies and replicas | Regression observed 11 approvals against limit 10; customer-wide transaction lock now passes |
| Retention removes command evidence and stalls all due timeouts | Regression failed before the missing-row handling; now passes for both affected and unrelated payments |
| An old freeze retry can undo a newer unfreeze | Awaiting approval for durable account_status_requests metadata |
| A refund stays Processing after consumer retries exhaust | Awaiting approval for durable refund timeout correlation |
| Abandoned Redis leases block retries for 24 hours | Bounded 30-second leases and unique ownership; before/after database commit and successor-owner tests pass |
| Database velocity mixes requestedAt with evaluatedAt | Awaiting approval for persisted requested_at and consistent request-time counting |
| Gateway deadline returns 503 instead of 504 | Stalled Kestrel upstream reproduced 503; timeout-feature classification now passes a full 504 problem response test |

Primary locations are `Features/AccountReplica/AccountStatusChangedHandler.cs`,
Risk's `Features/Evaluate/{EvaluateRiskHandler,DatabaseVelocityCounter}.cs`,
`Sagas/SagaTimeoutPoller.cs`, Accounts' `Features/Accounts/FreezeEndpoint.cs`,
`Features/Refunds/{CreateRefundEndpoint,RefundSaga}.cs`, Web's
`Idempotency/RedisIdempotencyStore.cs`, and Gateway's `Program.cs`.

The targeted tests above passed after their failures were observed. The whole
solution then passed 497/497 tests, with zero failures/skips, in 8m12s after these
independent fixes. The solution build had zero warnings/errors. A further complete
suite is required after the approved schema-dependent fixes. No second independent
review should be dispatched; finish this review's one regression-tested fix pass.

## Pending approval package

The user's global AGENTS instructions require approval before schema/migration
and public API changes. The bundled question supersedes the earlier question
about account status Version alone. No new external dependencies are needed.

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

Required regressions include reversed/concurrent status delivery, freeze replay
after an intervening unfreeze, cross-account key reuse with Redis unavailable,
delayed Risk requests spanning UTC midnight, and refund recovery after five
consumer failures and subsequent recovery. Preserve parent-payment-first locking.

## Accepted scope boundaries

M3 telemetry/load/security expansion and system failure suites, later reporting and
reconciliation, Kubernetes, production identity hardening and real-acquirer
compatibility remain outside M2. SMTP is at-least-once; Gateway rate limits are
per instance. These decisions and their costs are in [M2 decisions](m2-decisions.md).

Do not start M3. After approval and the remaining fixes: run the full suite and
fresh smoke as needed, update the handoff, open the M2 PR, wait for build/test and
GitGuardian checks, merge preserving the task commits, then pause.

## Inherited M1 minor findings

- Broaden amount-edge theories and complete problem-shape assertions across all POSTs.
- Remove incidental trailing blank lines at end of files when a later scoped edit permits it.

These are carried from M1, not newly reported M2 findings. Do not expand the M2
Important-finding fix pass to incidental cleanup.
