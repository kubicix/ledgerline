// broker-outage.js: 50 payments per second for 3 minutes (alternating top-ups and transfers), with RabbitMQ stopped
// for 30 s in the middle. Shows that acceptance continues while the broker is down (the POST only writes the payment
// and its outbox row) and how long the backlog takes to drain once it is back. Thresholds: docs/testing.md section 5,
// including recovery after a 30 s broker outage (backlog drained) < 60 s.
//
// k6 cannot stop a container, so the broker is stopped from outside, at a time this script fixes:
// - Scripted: scripts/broker-outage.sh runs this scenario through scripts/k6.sh and stops and starts the `rabbitmq`
//   service of the Compose project (LEDGERLINE_PROJECT) when the window opens and closes.
// - Manual: run `scripts/k6.sh broker-outage` (or scripts/k6.ps1). When the log shows
//   "broker-outage: load phase starts", wait OUTAGE_START_S seconds (default 75), run
//   `docker compose -p <project> --profile app stop rabbitmq`, and OUTAGE_DURATION_S seconds (default 30) after the
//   stop was issued run `docker compose -p <project> --profile app start --wait rabbitmq`.
// OUTAGE_START_S and OUTAGE_DURATION_S are passed as `--env NAME=value` k6 arguments; the defaults put the window in
// the middle of the 3 minute load phase. The window is measured from the end of setup, which this script logs.
//
// Two scenarios share the run:
// - `offer` POSTs 50 payments per second and does not poll them: a 30 s stall of sagas polled every 100 ms would need
//   thousands of VUs and push every user past the gateway's per-user limit. Each iteration waits 1 s after its POST,
//   so a VU sends at most one POST per second for its own customer user. outage_window_posts and
//   outage_window_accepted count the POSTs sent while the broker is planned to be down and the ones answered 202.
// - `saga_probe` starts one payment per second and polls it every 100 ms until it is final (saga_completion_ms). The
//   poll timeout is raised to OUTAGE_SAGA_TIMEOUT_MS (default 150 s) so that sagas stalled by the outage are measured.
//   outage_recovery_ms records, for every probe accepted before the window closed, how long after the window closed
//   it finished: its maximum is the time the backlog took to drain, broker start-up included.
import { sleep } from 'k6';
import exec from 'k6/execution';
import { Counter, Trend } from 'k6/metrics';
import { seedTokens } from './lib/auth.js';
import { topUpBody, transferBody } from './lib/api.js';
import { acceptSaga, gracefulStopFor, runSaga } from './lib/saga.js';
import { TEARDOWN_TIMEOUT, randomAmountMinor, randomOtherWallet, setupRun, summaryWithoutSetupData, teardownRun, walletOfVu } from './lib/setup.js';
import { SUMMARY_TREND_STATS, thresholdsFor } from './lib/thresholds.js';

const OUTAGE_START_S = Number(__ENV.OUTAGE_START_S || 75);
const OUTAGE_DURATION_S = Number(__ENV.OUTAGE_DURATION_S || 30);
const PROBE_TIMEOUT_MS = Number(__ENV.OUTAGE_SAGA_TIMEOUT_MS || 150000);
// Recovery after a 30 s broker outage (backlog drained): < 60 s (docs/testing.md section 5).
const RECOVERY_TARGET_MS = 60000;
const PACING_S = 1;
const DURATION = '3m';

const outageRecovery = new Trend('outage_recovery_ms', true);
const outageRecoverySamples = new Counter('outage_recovery_samples');
const outageWindowPosts = new Counter('outage_window_posts');
const outageWindowAccepted = new Counter('outage_window_accepted');

const thresholds = thresholdsFor(['topup', 'transfer']);
thresholds.outage_recovery_ms = [`max<${RECOVERY_TARGET_MS}`];
// An empty trend passes max<x, so at least one probe must have spanned the window.
thresholds.outage_recovery_samples = ['count>0'];

export const options = {
  scenarios: {
    offer: {
      executor: 'constant-arrival-rate', rate: 50, timeUnit: '1s', duration: DURATION,
      // An iteration is the POST plus 1 s, so 50 per second needs a little over 50 VUs; dropped_iterations reports a
      // shortfall when accepts slow down.
      preAllocatedVUs: 80, maxVUs: 300, exec: 'offer', gracefulStop: '30s', tags: { phase: 'load' },
    },
    saga_probe: {
      executor: 'constant-arrival-rate', rate: 1, timeUnit: '1s', duration: DURATION,
      preAllocatedVUs: 20, maxVUs: Math.ceil(PROBE_TIMEOUT_MS / 1000) + 20, exec: 'probe',
      gracefulStop: gracefulStopFor(PROBE_TIMEOUT_MS, 1), tags: { phase: 'load' },
    },
  },
  thresholds,
  summaryTrendStats: SUMMARY_TREND_STATS,
  setupTimeout: '600s',
  // Covers teardown's wait for the backlog to drain (lib/setup.js).
  teardownTimeout: TEARDOWN_TIMEOUT,
};

export function setup() {
  const data = setupRun('outage');
  // The load phase starts when setup returns; scripts/broker-outage.sh waits for this line.
  const loadStartsAt = Date.now();
  data.outageStartsAt = loadStartsAt + OUTAGE_START_S * 1000;
  data.outageEndsAt = data.outageStartsAt + OUTAGE_DURATION_S * 1000;
  console.log(`broker-outage: load phase starts; broker planned down from ${new Date(data.outageStartsAt).toISOString()} ` +
    `to ${new Date(data.outageEndsAt).toISOString()} (${OUTAGE_START_S} s after this line, for ${OUTAGE_DURATION_S} s)`);
  return data;
}

/** Alternates top-ups and transfers by iteration, from the VU's customer wallet. */
function payment(data, iteration) {
  const from = walletOfVu(data.customers);
  if (iteration % 2 === 0) {
    return { kind: 'topup', user: from.user, path: '/api/topups', body: topUpBody(from.id, randomAmountMinor(100, 1000), from.currency) };
  }
  const to = randomOtherWallet(data.customers, from);
  return { kind: 'transfer', user: from.user, path: '/api/transfers', body: transferBody(from.id, to.id, randomAmountMinor(1, 100), from.currency) };
}

export function offer(data) {
  seedTokens(data.tokens);
  const iteration = exec.scenario.iterationInTest;
  const p = payment(data, iteration);
  const sentAt = Date.now();
  const id = acceptSaga(p.kind, p.user, p.path, p.body, `k6-outage-${data.runId}-${iteration}`);
  if (sentAt >= data.outageStartsAt && sentAt < data.outageEndsAt) {
    outageWindowPosts.add(1);
    if (id !== null) outageWindowAccepted.add(1);
  }
  sleep(PACING_S);
}

export function probe(data) {
  seedTokens(data.tokens);
  const iteration = exec.scenario.iterationInTest;
  const p = payment(data, iteration);
  const sentAt = Date.now();
  const status = runSaga(p.kind, p.user, p.path, p.body, `k6-outage-p-${data.runId}-${iteration}`, { timeoutMs: PROBE_TIMEOUT_MS });
  const finishedAt = Date.now();
  if (status !== 'NotAccepted' && sentAt < data.outageEndsAt && finishedAt > data.outageEndsAt) {
    outageRecovery.add(finishedAt - data.outageEndsAt);
    outageRecoverySamples.add(1);
  }
}

export function teardown(data) {
  teardownRun(data);
}

export function handleSummary(data) {
  return summaryWithoutSetupData(data);
}
