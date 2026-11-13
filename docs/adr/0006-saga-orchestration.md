# ADR 0006: Orchestrated saga in Payments, hand-written state machine

Date: 2026-11-13. Status: accepted.

## Context

A payment spans Risk, the bank, and the ledger. There is no distributed transaction.
The flow needs ordered steps, timeouts, retries, and compensation.

Choreography (each service reacts to the previous service's event) and orchestration
(one coordinator sends commands and waits for replies) were compared.

## Decision

Orchestration. Payments is the orchestrator. The payment row is the saga instance.

- State lives in `payments` (`status`, `saga_step`, `version`) in `payments_db`.
- Each transition is one local transaction: inbox row, state change, next command in
  the outbox, timeout row.
- Transitions are guarded by the current step. A reply that does not match the
  current step is acknowledged and ignored (late or duplicate).
- Calls to the bank are made by a consumer of an internal command
  (`payments.command.call-bank.v1`), never inside the transaction that decided to make
  the call. The bank request carries an idempotency key derived from the payment ID
  and the step, so a redelivered command cannot charge twice.
- Timeouts: `saga_timeouts` rows are polled by a hosted service. A timeout re-sends
  the step's command with the same `operationId` (safe by idempotency). After the
  retry limit the payment moves to `ManualReview` and a metric fires.
- Unknown outcome at the bank (timeout after sending): query the bank by idempotency
  key first. Only a confirmed "not found" is treated as "not done".

Risk is called asynchronously through the same mechanism, not by HTTP. A synchronous
risk check would make Payments' availability depend on Risk and would hide the
"service is down, payments wait and then finish" behavior the project wants to prove.
The cost is latency on the happy path, which k6 measures.

## Why not choreography

- The business rule "what happens after what" would be spread across four services.
- Compensation order is hard to see and hard to test.
- With one orchestrator, the whole flow is one state diagram, one table to inspect,
  and one place to put timeouts.

Choreography remains in use where it fits: Notifications and (Phase 2) Reporting and
Reconciliation simply subscribe to events. Nobody waits for them.

## Why hand-written and not a saga library

Same reasoning as ADR 0002. The state machine is a transition table plus guards. It is
unit-testable without infrastructure: given state and message, expect new state and
outgoing commands.

## Consequences

- Payments knows the names of commands in other services' contracts. Accepted: that
  is what an orchestrator is.
- Every step needs: command, success reply, failure reply, timeout, and (if it has an
  external side effect) a compensation. The spec lists all of them per payment kind.
- `ManualReview` is a real terminal state. The system is allowed to say "a human must
  look at this" instead of guessing about money.
