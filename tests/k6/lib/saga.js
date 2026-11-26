// Starts a payment saga through the gateway and measures saga_completion_ms: from the accepted POST until
// GET /api/payments/{id} returns a final status, polled every 100 ms (testing.md section 5).
import { sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { getPayment, post } from './api.js';
import { SAGA_TARGET_P95_MS } from './thresholds.js';

export const sagaCompletion = new Trend('saga_completion_ms', true);
// Every saga that reached Completed, per kind. Thresholds require it to be non-zero so that an empty
// saga_completion_ms sub-metric (which passes any p(95) threshold) cannot pass a run.
export const sagaCompleted = new Counter('saga_completed');
// Every final status other than Completed, tagged with kind and status. Visible in the summary.
export const sagaUnsuccessful = new Counter('saga_unsuccessful');
export const sagaManualReview = new Counter('saga_manual_review');
// Sagas still in flight when polling gave up. Also added to saga_completion_ms with the time waited.
export const sagaTimeouts = new Counter('saga_timeouts');
// POSTs that were not accepted (anything but 202). The response also counts in http_req_failed.
export const acceptFailed = new Counter('accept_failed');

const FINAL = new Set(['Completed', 'Captured', 'Voided', 'Compensated', 'Rejected', 'Failed', 'ManualReview']);
const POLL_INTERVAL_S = 0.1;
// How long one saga is polled before it counts as timed out. A timed-out saga is recorded in saga_completion_ms with
// the time waited, so a timeout shorter than a target would record stalled sagas below that target and could turn a
// miss into a pass. SAGA_TIMEOUT_MS can therefore only raise the timeout above twice the largest p(95) target.
const REQUESTED_TIMEOUT_MS = Number(__ENV.SAGA_TIMEOUT_MS || 30000);
const MIN_TIMEOUT_MS = 2 * Math.max(...Object.values(SAGA_TARGET_P95_MS));
const POLL_TIMEOUT_MS = Math.max(REQUESTED_TIMEOUT_MS || 0, MIN_TIMEOUT_MS);

/** The poll timeout a saga uses: a caller's timeoutMs can only raise it, never lower it below POLL_TIMEOUT_MS. */
const timeoutOf = (timeoutMs) => Math.max(POLL_TIMEOUT_MS, Number(timeoutMs) || 0);

/**
 * Scenario gracefulStop for iterations that run `sagas` sagas one after another, each polled up to `timeoutMs`
 * (raised to POLL_TIMEOUT_MS): long enough for the last one started before the end to finish or time out.
 */
export function gracefulStopFor(timeoutMs, sagas) {
  return `${(sagas || 1) * Math.ceil(timeoutOf(timeoutMs) / 1000) + 10}s`;
}

/** Scenario gracefulStop: long enough for a saga started just before the end to finish or time out and be recorded. */
export const SAGA_GRACEFUL_STOP = gracefulStopFor(0, 1);

/** Called once from setup(): says when SAGA_TIMEOUT_MS was raised to the minimum. */
export function logSagaTimeout() {
  if (POLL_TIMEOUT_MS !== REQUESTED_TIMEOUT_MS) {
    console.warn(`SAGA_TIMEOUT_MS ${__ENV.SAGA_TIMEOUT_MS} raised to ${POLL_TIMEOUT_MS} ms (twice the largest saga target)`);
  }
}

/** The payment's status, or null while GET /api/payments/{id} does not answer 200. */
const paymentStatus = (user, id) => {
  const res = getPayment(user, id);
  return res.status === 200 ? res.json('status') : null;
};

/**
 * POSTs a request that starts a saga step (tag op:accept, which the POST accept latency thresholds select). `name`
 * is the request name tag for a path with an id in it (default: the path). Returns the accepted resource's id, or
 * null when the answer was not 202 (counted in accept_failed).
 */
export function acceptSaga(kind, user, path, body, idempotencyKey, name) {
  const accepted = post(user, path, body, idempotencyKey, name || path, { op: 'accept', kind });
  if (accepted.status !== 202) {
    acceptFailed.add(1, { kind, status: String(accepted.status) });
    return null;
  }
  return accepted.json('id');
}

/**
 * Polls an accepted saga every 100 ms with the user's token and records it in saga_completion_ms. Options, all
 * optional (the defaults are the payment sagas that end Completed):
 * - success: the status that counts as completed (default 'Completed'). Polling stops at it or at any final status.
 * - read(user, id): returns the current status or null (default: GET /api/payments/{id}).
 * - timeoutMs: raises the poll timeout for this saga (it never goes below POLL_TIMEOUT_MS).
 * Returns the status polling stopped at, or 'Timeout'.
 */
export function awaitSaga(kind, user, id, options) {
  const opts = options || {};
  const success = opts.success || 'Completed';
  const read = opts.read || paymentStatus;
  const timeoutMs = timeoutOf(opts.timeoutMs);
  const started = Date.now();
  for (;;) {
    sleep(POLL_INTERVAL_S);
    const status = read(user, id);
    if (status && (status === success || FINAL.has(status))) {
      const elapsed = Date.now() - started;
      if (status === success) {
        sagaCompletion.add(elapsed, { kind });
        sagaCompleted.add(1, { kind });
      } else {
        sagaUnsuccessful.add(1, { kind, status });
        if (status === 'ManualReview') sagaManualReview.add(1, { kind });
      }
      return status;
    }
    if (Date.now() - started >= timeoutMs) {
      sagaCompletion.add(Date.now() - started, { kind });
      sagaTimeouts.add(1, { kind });
      return 'Timeout';
    }
  }
}

/**
 * POSTs the payment (acceptSaga) and polls it with the caller's token until it is final (awaitSaga, same options).
 * Returns the final status, 'NotAccepted' or 'Timeout'.
 */
export function runSaga(kind, user, path, body, idempotencyKey, options) {
  const id = acceptSaga(kind, user, path, body, idempotencyKey);
  return id === null ? 'NotAccepted' : awaitSaga(kind, user, id, options);
}
