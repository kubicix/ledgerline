// topup-steady.js: 50 top-ups per second for 5 minutes. Shows the full saga with the bank step.
// Thresholds: docs/testing.md section 5. DURATION overrides the length for a quick check only.
import exec from 'k6/execution';
import { seedTokens } from './lib/auth.js';
import { topUpBody } from './lib/api.js';
import { SAGA_GRACEFUL_STOP, runSaga } from './lib/saga.js';
import { TEARDOWN_TIMEOUT, randomAmountMinor, setupRun, summaryWithoutSetupData, teardownRun, walletOfVu } from './lib/setup.js';
import { SUMMARY_TREND_STATS, thresholdsFor } from './lib/thresholds.js';

export const options = {
  scenarios: {
    topup_steady: {
      executor: 'constant-arrival-rate', rate: 50, timeUnit: '1s', duration: __ENV.DURATION || '5m',
      // Rate times the 2 s saga target, with headroom; dropped_iterations reports a shortfall.
      preAllocatedVUs: 120, maxVUs: 300, exec: 'topUp', gracefulStop: SAGA_GRACEFUL_STOP, tags: { phase: 'load' },
    },
  },
  thresholds: thresholdsFor(['topup']),
  summaryTrendStats: SUMMARY_TREND_STATS,
  setupTimeout: '600s',
  // Covers teardown's wait for the backlog to drain (lib/setup.js).
  teardownTimeout: TEARDOWN_TIMEOUT,
};

export function setup() {
  return setupRun('topup-steady');
}

export function topUp(data) {
  seedTokens(data.tokens);
  const wallet = walletOfVu(data.customers);
  const key = `k6-topup-steady-${data.runId}-${exec.scenario.iterationInTest}`;
  runSaga('topup', wallet.user, '/api/topups', topUpBody(wallet.id, randomAmountMinor(100, 1000), wallet.currency), key);
}

export function teardown(data) {
  teardownRun(data);
}

export function handleSummary(data) {
  return summaryWithoutSetupData(data);
}
