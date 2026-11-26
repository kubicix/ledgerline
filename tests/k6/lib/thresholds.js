// Thresholds are the tests of the k6 scenarios. Values are the targets of docs/testing.md section 5, fixed before any
// measurement: never change one to make a run pass; a missed target is published as a miss.
//
// Load requests carry the scenario tag phase:load, so setup and teardown traffic is not measured. POST requests that
// start a saga carry op:accept. http_req_failed counts only unexpected responses: k6 expects 200 to 399 by default,
// and the only expected 4xx (404 while a new wallet's replica is missing) is retried in setup, outside phase:load.
// 429 from the gateway rate limiter is not expected and counts as a failure.
//
// Not expressed here: ledgerline.outbox.lag p95 (a service metric, read from Prometheus) and sustained transfer
// throughput (accepted transfers over the 5 minute window, read from the summary; dropped_iterations shows a shortfall).

export const SAGA_TARGET_P95_MS = { transfer: 1000, topup: 2000 };

/** Thresholds every scenario shares. */
export function baseThresholds() {
  return {
    'http_req_failed{phase:load}': ['rate<0.001'],
    'http_req_duration{phase:load,op:accept}': ['p(95)<150', 'p(99)<400'],
    // Payments in ManualReview after a clean run: 0.
    saga_manual_review: ['count==0'],
    // Trial balance after every run: 0. The check must have run once.
    trial_balance_checks: ['count==1'],
    trial_balance_nonzero: ['count==0'],
  };
}

/** saga_completion_ms p95 for each kind the scenario runs, plus a non-empty guard (an empty sub-metric passes p(95)). */
export function sagaThresholds(kinds) {
  const result = {};
  for (const kind of kinds) {
    result[`saga_completion_ms{kind:${kind}}`] = [`p(95)<${SAGA_TARGET_P95_MS[kind]}`];
    result[`saga_completed{kind:${kind}}`] = ['count>0'];
  }
  return result;
}

export function thresholdsFor(kinds) {
  return Object.assign(baseThresholds(), sagaThresholds(kinds));
}

// p(99) is a target, so the summary (and the --summary-export JSON) must report it; k6 shows p(90) and p(95) by default.
export const SUMMARY_TREND_STATS = ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'];
