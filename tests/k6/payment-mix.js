// payment-mix.js: 50 operations per second for 5 minutes, split by iteration number into exactly
//   60% authorize (Manual capture) and capture, 20% authorize and void, 10% refund, 10% top-up.
// Shows a realistic mix with the system accounts (PAYMENTS_PENDING for holds, BANK_CLEARING for top-ups) under load.
// Thresholds: docs/testing.md section 5. DURATION overrides the length for a quick check only.
//
// The VU's customer creates and pays; the VU's merchant user (VU n acts for merchant (n - 1) mod 10, so a VU holds
// only two tokens) captures, voids and refunds, which the API allows only the merchant wallet's owner to do. Every
// status is polled with the customer's token (the payment's customer may read the payment and its refunds), so the 10
// merchant users only send the capture, void and refund POSTs and stay far below the gateway's per-user limit.
// A refund needs a captured payment, so a refund iteration first runs an Automatic-capture payment, then refunds part
// of it. saga_completion_ms is tagged by step: authorize (until Authorized), capture (until Captured), void (until
// Voided), autocapture (until Captured), refund (until the refund is Completed) and topup (until Completed).
import exec from 'k6/execution';
import { seedTokens } from './lib/auth.js';
import { listRefunds, merchantPaymentBody, topUpBody } from './lib/api.js';
import { acceptSaga, awaitSaga, gracefulStopFor, runSaga } from './lib/saga.js';
import { TEARDOWN_TIMEOUT, randomAmountMinor, setupRun, summaryWithoutSetupData, teardownRun, walletOfVu } from './lib/setup.js';
import { SUMMARY_TREND_STATS, thresholdsFor } from './lib/thresholds.js';

const MERCHANT_KINDS = ['authorize', 'capture', 'void', 'autocapture', 'refund'];

// testing.md section 5 sets a saga target only for transfers and top-ups. The merchant steps get no latency threshold,
// only the same non-empty guard as the other kinds: a run in which a step never completes must not pass.
const thresholds = thresholdsFor(['topup']);
for (const kind of MERCHANT_KINDS) thresholds[`saga_completed{kind:${kind}}`] = ['count>0'];

export const options = {
  scenarios: {
    payment_mix: {
      executor: 'constant-arrival-rate', rate: 50, timeUnit: '1s', duration: __ENV.DURATION || '5m',
      // An iteration runs up to three sagas one after another (about 2 s at the targets). 400 VUs are 2 per customer
      // user, about 22 requests/s per user with 100 ms polling, below the gateway's 50/s per-user limit.
      preAllocatedVUs: 150, maxVUs: 400, exec: 'mix', gracefulStop: gracefulStopFor(0, 3), tags: { phase: 'load' },
    },
  },
  thresholds,
  summaryTrendStats: SUMMARY_TREND_STATS,
  setupTimeout: '600s',
  // Covers teardown's wait for the backlog to drain (lib/setup.js).
  teardownTimeout: TEARDOWN_TIMEOUT,
};

export function setup() {
  return setupRun('mix');
}

const refundStatus = (paymentId) => (user, refundId) => {
  const res = listRefunds(user, paymentId);
  if (res.status !== 200) return null;
  const refund = res.json('items').find((r) => r.id === refundId);
  return refund ? refund.status : null;
};

/** Customer POST /api/payments, polled until Authorized (Manual) or Captured (Automatic). Returns the id or null. */
function pay(kind, customer, merchant, amountMinor, captureMode, key) {
  const id = acceptSaga(kind, customer.user, '/api/payments',
    merchantPaymentBody(customer.id, merchant.id, amountMinor, customer.currency, captureMode), key);
  if (id === null) return null;
  const done = captureMode === 'Manual' ? 'Authorized' : 'Captured';
  return awaitSaga(kind, customer.user, id, { success: done }) === done ? id : null;
}

/** Merchant POST of a capture, void or refund; the customer polls it until `success`. */
function merchantStep(kind, merchant, customer, id, action, body, key, success, read) {
  const stepId = acceptSaga(kind, merchant.user, `/api/payments/${id}/${action}`, body, key, `/api/payments/{id}/${action}`);
  if (stepId !== null) awaitSaga(kind, customer.user, stepId, { success, read });
}

export function mix(data) {
  seedTokens(data.tokens);
  const iteration = exec.scenario.iterationInTest;
  const customer = walletOfVu(data.customers);
  const merchant = walletOfVu(data.merchants);
  const key = `k6-mix-${data.runId}-${iteration}`;
  const amountMinor = randomAmountMinor(100, 1000);
  const slot = iteration % 10;
  if (slot < 6) {
    const id = pay('authorize', customer, merchant, amountMinor, 'Manual', key);
    if (id !== null) merchantStep('capture', merchant, customer, id, 'capture', { amountMinor }, `${key}-c`, 'Captured');
  } else if (slot < 8) {
    const id = pay('authorize', customer, merchant, amountMinor, 'Manual', key);
    if (id !== null) merchantStep('void', merchant, customer, id, 'void', {}, `${key}-v`, 'Voided');
  } else if (slot < 9) {
    const id = pay('autocapture', customer, merchant, amountMinor, 'Automatic', key);
    if (id !== null) {
      merchantStep('refund', merchant, customer, id, 'refunds', { amountMinor: randomAmountMinor(1, amountMinor) },
        `${key}-r`, 'Completed', refundStatus(id));
    }
  } else {
    runSaga('topup', customer.user, '/api/topups', topUpBody(customer.id, amountMinor, customer.currency), key);
  }
}

export function teardown(data) {
  teardownRun(data);
}

export function handleSummary(data) {
  return summaryWithoutSetupData(data);
}
