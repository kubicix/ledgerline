// transfer-steady.js: 100 transfers per second for 5 minutes from each VU's customer wallet to a random other
// customer wallet.
// Shows ledger throughput without the bank. Thresholds: docs/testing.md section 5.
// DURATION overrides the length for a quick check only.
import exec from 'k6/execution';
import { seedTokens } from './lib/auth.js';
import { transferBody } from './lib/api.js';
import { SAGA_GRACEFUL_STOP, runSaga } from './lib/saga.js';
import { randomAmountMinor, randomOtherWallet, setupRun, summaryWithoutSetupData, teardownRun, walletOfVu } from './lib/setup.js';
import { SUMMARY_TREND_STATS, thresholdsFor } from './lib/thresholds.js';

export const options = {
  scenarios: {
    transfer_steady: {
      executor: 'constant-arrival-rate', rate: 100, timeUnit: '1s', duration: __ENV.DURATION || '5m',
      // Rate times the 1 s saga target, with headroom; dropped_iterations reports a shortfall.
      preAllocatedVUs: 150, maxVUs: 400, exec: 'transfer', gracefulStop: SAGA_GRACEFUL_STOP, tags: { phase: 'load' },
    },
  },
  thresholds: thresholdsFor(['transfer']),
  summaryTrendStats: SUMMARY_TREND_STATS,
  setupTimeout: '600s',
};

export function setup() {
  return setupRun('transfer-steady');
}

export function transfer(data) {
  seedTokens(data.tokens);
  const from = walletOfVu(data.customers);
  const to = randomOtherWallet(data.customers, from);
  const key = `k6-transfer-steady-${data.runId}-${exec.scenario.iterationInTest}`;
  runSaga('transfer', from.user, '/api/transfers', transferBody(from.id, to.id, randomAmountMinor(1, 100), from.currency), key);
}

export function teardown(data) {
  teardownRun(data);
}

export function handleSummary(data) {
  return summaryWithoutSetupData(data);
}
