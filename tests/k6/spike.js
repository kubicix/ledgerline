// spike.js: transfers between customer wallets, offered at 20 per second, raised to 300 per second in 10 s, held for
// 60 s, and brought back to 20 per second in 10 s. Shows the backlog and the recovery; read ledgerline.outbox.lag
// from Prometheus for the same window. Thresholds: docs/testing.md section 5.
//
// Two scenarios share the run:
// - `spike` offers the load. Each iteration POSTs one transfer and does not poll it: at 300 per second a backlog of
//   sagas polled every 100 ms would need thousands of VUs and turn the polls, not the transfers, into the load. Each
//   iteration then waits 1 s, so a VU sends at most one POST per second for its own customer user and the gateway's
//   per-user limit (50 requests/s) is never what the run measures.
// - `saga_probe` starts one transfer per second for the whole run and polls it every 100 ms until it is final. These
//   sagas are the saga_completion_ms samples: they show how long a transfer takes before, during and after the spike.
//   Their poll timeout is raised to SPIKE_SAGA_TIMEOUT_MS (default 120 s) so a long backlog is measured, not cut off.
// The 30 s at 20 per second before the spike and the 60 s after it are the baseline and the recovery window.
import { sleep } from 'k6';
import exec from 'k6/execution';
import { seedTokens } from './lib/auth.js';
import { transferBody } from './lib/api.js';
import { acceptSaga, gracefulStopFor, runSaga } from './lib/saga.js';
import { TEARDOWN_TIMEOUT, randomAmountMinor, randomOtherWallet, setupRun, summaryWithoutSetupData, teardownRun, walletOfVu } from './lib/setup.js';
import { SUMMARY_TREND_STATS, thresholdsFor } from './lib/thresholds.js';

const PROBE_TIMEOUT_MS = Number(__ENV.SPIKE_SAGA_TIMEOUT_MS || 120000);
const PACING_S = 1;
const STAGES = [
  { duration: '30s', target: 20 },
  { duration: '10s', target: 300 },
  { duration: '60s', target: 300 },
  { duration: '10s', target: 20 },
  { duration: '60s', target: 20 },
];
const TOTAL = '170s';

export const options = {
  scenarios: {
    spike: {
      executor: 'ramping-arrival-rate', startRate: 20, timeUnit: '1s', stages: STAGES,
      // An iteration is the POST plus 1 s, so 300 per second needs a little over 300 VUs. 800 VUs are 4 per customer
      // user, at most 4 POSTs/s per user. dropped_iterations reports a shortfall when accepts slow down.
      preAllocatedVUs: 350, maxVUs: 800, exec: 'offer', gracefulStop: '30s', tags: { phase: 'load' },
    },
    saga_probe: {
      executor: 'constant-arrival-rate', rate: 1, timeUnit: '1s', duration: TOTAL,
      // One saga per second, each polled for up to the probe timeout.
      preAllocatedVUs: 20, maxVUs: Math.ceil(PROBE_TIMEOUT_MS / 1000) + 20, exec: 'probe',
      gracefulStop: gracefulStopFor(PROBE_TIMEOUT_MS, 1), tags: { phase: 'load' },
    },
  },
  thresholds: thresholdsFor(['transfer']),
  summaryTrendStats: SUMMARY_TREND_STATS,
  setupTimeout: '600s',
  // Covers teardown's wait for the backlog to drain (lib/setup.js).
  teardownTimeout: TEARDOWN_TIMEOUT,
};

export function setup() {
  return setupRun('spike');
}

function transferFromVu(data) {
  const from = walletOfVu(data.customers);
  const to = randomOtherWallet(data.customers, from);
  return { from, body: transferBody(from.id, to.id, randomAmountMinor(1, 100), from.currency) };
}

export function offer(data) {
  seedTokens(data.tokens);
  const { from, body } = transferFromVu(data);
  acceptSaga('transfer', from.user, '/api/transfers', body, `k6-spike-${data.runId}-${exec.scenario.iterationInTest}`);
  sleep(PACING_S);
}

export function probe(data) {
  seedTokens(data.tokens);
  const { from, body } = transferFromVu(data);
  runSaga('transfer', from.user, '/api/transfers', body, `k6-spike-p-${data.runId}-${exec.scenario.iterationInTest}`,
    { timeoutMs: PROBE_TIMEOUT_MS });
}

export function teardown(data) {
  teardownRun(data);
}

export function handleSummary(data) {
  return summaryWithoutSetupData(data);
}
