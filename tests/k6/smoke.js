// smoke.js: 5 VUs for 60 s, alternating top-ups (full saga with the bank step) and transfers between customers.
// Each VU acts for its own customer user (walletOfVu).
// Shows the stack works; used in CI. Thresholds: docs/testing.md section 5.
import { sleep } from 'k6';
import exec from 'k6/execution';
import { seedTokens } from './lib/auth.js';
import { topUpBody, transferBody } from './lib/api.js';
import { SAGA_GRACEFUL_STOP, runSaga } from './lib/saga.js';
import { randomAmountMinor, randomOtherWallet, setupRun, summaryWithoutSetupData, teardownRun, walletOfVu } from './lib/setup.js';
import { SUMMARY_TREND_STATS, thresholdsFor } from './lib/thresholds.js';

export const options = {
  scenarios: {
    smoke: {
      executor: 'constant-vus', vus: 5, duration: __ENV.DURATION || '60s', exec: 'smoke',
      // In-flight sagas at the end are recorded (completed or timed out), not cut off.
      gracefulStop: SAGA_GRACEFUL_STOP, tags: { phase: 'load' },
    },
  },
  thresholds: thresholdsFor(['topup', 'transfer']),
  summaryTrendStats: SUMMARY_TREND_STATS,
  setupTimeout: '600s',
};

export function setup() {
  return setupRun('smoke');
}

export function smoke(data) {
  seedTokens(data.tokens);
  const key = `k6-smoke-${data.runId}-${exec.vu.idInTest}-${exec.scenario.iterationInTest}`;
  if (exec.scenario.iterationInTest % 2 === 0) {
    const wallet = walletOfVu(data.customers);
    runSaga('topup', wallet.user, '/api/topups', topUpBody(wallet.id, randomAmountMinor(100, 1000), wallet.currency), key);
  } else {
    const from = walletOfVu(data.customers);
    const to = randomOtherWallet(data.customers, from);
    runSaga('transfer', from.user, '/api/transfers', transferBody(from.id, to.id, randomAmountMinor(1, 100), from.currency), key);
  }
  sleep(0.5);
}

export function teardown(data) {
  teardownRun(data);
}

export function handleSummary(data) {
  return summaryWithoutSetupData(data);
}
