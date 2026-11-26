// transfer-hot-account.js: 100 transfers per second for 3 minutes, every one from the VU's customer wallet into the same
// merchant wallet (the first merchant user's). Shows row-lock contention on a hot account: the ledger locks every
// account of a posting, so all transfers queue on that one row. Thresholds: docs/testing.md section 5.
// DURATION overrides the length for a quick check only.
import exec from 'k6/execution';
import { seedTokens } from './lib/auth.js';
import { transferBody } from './lib/api.js';
import { SAGA_GRACEFUL_STOP, runSaga } from './lib/saga.js';
import { TEARDOWN_TIMEOUT, randomAmountMinor, setupRun, summaryWithoutSetupData, teardownRun, walletOfVu } from './lib/setup.js';
import { SUMMARY_TREND_STATS, thresholdsFor } from './lib/thresholds.js';

export const options = {
  scenarios: {
    transfer_hot_account: {
      executor: 'constant-arrival-rate', rate: 100, timeUnit: '1s', duration: __ENV.DURATION || '3m',
      // Same sizing as transfer-steady.js; dropped_iterations reports a shortfall. 400 VUs are 2 per customer user,
      // about 22 requests/s per user with 100 ms polling, below the gateway's 50/s per-user limit.
      preAllocatedVUs: 150, maxVUs: 400, exec: 'transfer', gracefulStop: SAGA_GRACEFUL_STOP, tags: { phase: 'load' },
    },
  },
  thresholds: thresholdsFor(['transfer']),
  summaryTrendStats: SUMMARY_TREND_STATS,
  setupTimeout: '600s',
  // Covers teardown's wait for the backlog to drain (lib/setup.js).
  teardownTimeout: TEARDOWN_TIMEOUT,
};

export function setup() {
  return setupRun('hot');
}

export function transfer(data) {
  seedTokens(data.tokens);
  const from = walletOfVu(data.customers);
  const hot = data.merchants[0];
  const key = `k6-hot-${data.runId}-${exec.scenario.iterationInTest}`;
  runSaga('transfer', from.user, '/api/transfers', transferBody(from.id, hot.id, randomAmountMinor(1, 100), from.currency), key);
}

export function teardown(data) {
  teardownRun(data);
}

export function handleSummary(data) {
  return summaryWithoutSetupData(data);
}
