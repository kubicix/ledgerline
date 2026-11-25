// Shared setup() and teardown() for every scenario (testing.md section 5): tokens for the seeded load-test users through
// `ledgerline-k6`, one customer wallet per customer user (200) and one merchant wallet per merchant user (10), a top-up
// for each customer wallet, the Risk velocity rule raised for the run, and a closing trial balance check.
import { sleep } from 'k6';
import exec from 'k6/execution';
import { Counter } from 'k6/metrics';
import { USERS, loginAll, seedTokens } from './auth.js';
import { get, getPayment, getTrialBalance, listAccounts, openWallet, postWithRetry, put, topUpBody } from './api.js';
import { logSagaTimeout } from './saga.js';

// Every wallet uses one currency, so any two customer wallets can transfer to each other.
export const CURRENCY = 'TRY';
// A customer wallet below the floor is topped up by TOPUP_MINOR before the load starts. Both are far below Risk's
// seeded MaxSingleAmount (5 000 000) and DailyAmountLimit (20 000 000 per customer, day and currency).
const BALANCE_FLOOR_MINOR = 500000;
const TOPUP_MINOR = 1000000;

// Risk's velocity rule as seeded (10 per 60 s per customer) would reject most payments of a load run, which then
// measures the rule instead of the saga. Setup raises it to the same sentinel the system tests use and teardown
// restores it. Risk caches rules for 10 s, so setup waits that long after the change.
const RAISED_VELOCITY_MAX_COUNT = 100000;
const SEEDED_VELOCITY = { maxCount: 10, windowSeconds: 60 };
const RULE_CACHE_WAIT_S = 11;

const trialBalanceChecks = new Counter('trial_balance_checks');
const trialBalanceNonZero = new Counter('trial_balance_nonzero');

/** The user's wallet of this type in CURRENCY, opened when missing (a user holds one per type and currency). */
function walletOf(user, type, key, runId) {
  const res = listAccounts(user);
  if (res.status !== 200) throw new Error(`GET /api/accounts failed with ${res.status}`);
  let wallet = res.json().find((a) => a.type === type && a.currency === CURRENCY);
  if (!wallet) {
    const opened = openWallet(user, type, CURRENCY, `k6-${key}-wallet-${runId}-${user}`);
    if (opened.status !== 201) throw new Error(`Opening a ${type} failed with ${opened.status}`);
    wallet = opened.json();
  }
  return { user, id: wallet.id, currency: CURRENCY, balanceMinor: wallet.balanceMinor };
}

function waitCompleted(user, id) {
  const deadline = Date.now() + 60000;
  for (;;) {
    const res = getPayment(user, id);
    const status = res.status === 200 ? res.json('status') : null;
    if (status === 'Completed') return;
    if (status && !['Processing', 'Authorized', 'Capturing'].includes(status)) {
      throw new Error(`Setup top-up ${id} ended ${status}`);
    }
    if (Date.now() >= deadline) throw new Error(`Setup top-up ${id} is still ${status}`);
    sleep(0.2);
  }
}

function velocityRule(admin) {
  const res = get(admin, '/api/risk/rules', '/api/risk/rules', { op: 'setup' });
  if (res.status !== 200) throw new Error(`GET /api/risk/rules failed with ${res.status}`);
  return res.json('items').find((rule) => rule.code === 'VelocityLimit');
}

function updateRule(admin, rule, values) {
  const res = put(admin, `/api/risk/rules/${rule.id}`, {
    thresholdMinor: null, maxCount: values.maxCount, windowSeconds: values.windowSeconds, enabled: values.enabled,
  }, '/api/risk/rules/{id}');
  if (res.status !== 200) throw new Error(`PUT /api/risk/rules/{id} failed with ${res.status}`);
}

/** Raises the velocity rule and returns the values teardown must restore. */
function raiseVelocity(admin) {
  const rule = velocityRule(admin);
  // A rule that already carries the sentinel was left behind by a run that died before teardown: restore the seed.
  const restore = rule.maxCount === RAISED_VELOCITY_MAX_COUNT
    ? { id: rule.id, maxCount: SEEDED_VELOCITY.maxCount, windowSeconds: SEEDED_VELOCITY.windowSeconds, enabled: true }
    : { id: rule.id, maxCount: rule.maxCount, windowSeconds: rule.windowSeconds, enabled: rule.enabled };
  updateRule(admin, rule, { maxCount: RAISED_VELOCITY_MAX_COUNT, windowSeconds: rule.windowSeconds, enabled: true });
  sleep(RULE_CACHE_WAIT_S);
  return restore;
}

/** setup() body shared by every scenario. `key` keeps idempotency keys of different scenarios apart. */
export function setupRun(key) {
  const runId = __ENV.RUN_ID || String(Date.now());
  logSagaTimeout();
  const tokens = loginAll();
  seedTokens(tokens);
  const customers = USERS.customers.map((user) => walletOf(user, 'CustomerWallet', key, runId));
  const merchants = USERS.merchants.map((user) => walletOf(user, 'MerchantWallet', key, runId));
  console.log(`setup: ${customers.length} customer wallets, ${merchants.length} merchant wallets`);
  const velocityRestore = raiseVelocity(USERS.admin);
  try {
    const topUps = [];
    for (const wallet of customers) {
      if (wallet.balanceMinor >= BALANCE_FLOOR_MINOR) continue;
      const res = postWithRetry(wallet.user, '/api/topups', topUpBody(wallet.id, TOPUP_MINOR, wallet.currency),
        `k6-${key}-topup-${runId}-${wallet.user}`, '/api/topups');
      if (res.status !== 202) throw new Error(`Setup top-up failed with ${res.status}`);
      topUps.push({ user: wallet.user, id: res.json('id') });
    }
    for (const topUp of topUps) waitCompleted(topUp.user, topUp.id);
  } catch (error) {
    // teardown() does not run when setup() throws, so restore the rule here.
    updateRule(USERS.admin, velocityRestore, velocityRestore);
    throw error;
  }
  return {
    runId, tokens, velocityRestore,
    customers: customers.map(({ user, id, currency }) => ({ user, id, currency })),
    merchants: merchants.map(({ user, id, currency }) => ({ user, id, currency })),
  };
}

/** teardown() body: restores the velocity rule, then checks that every currency's trial balance sums to zero. */
export function teardownRun(data) {
  // tokenFor() renews the admin token if it expired during a long run.
  const admin = USERS.admin;
  if (data && data.velocityRestore) updateRule(admin, data.velocityRestore, data.velocityRestore);
  const res = getTrialBalance(admin);
  if (res.status !== 200) throw new Error(`GET /api/ledger/trial-balance failed with ${res.status}`);
  trialBalanceChecks.add(1);
  const nonZero = res.json('currencies').filter((c) => c.sumOfBalancesMinor !== 0);
  for (const currency of nonZero) {
    trialBalanceNonZero.add(1, { currency: currency.currency });
    console.error(`trial balance ${currency.currency} is ${currency.sumOfBalancesMinor}, expected 0`);
  }
  console.log(`teardown: trial balance checked for ${res.json('currencies').length} currencies, ${nonZero.length} non-zero`);
}

/**
 * The customer wallet this VU acts for: VU n uses customer ((n - 1) mod pool size). Spreading VUs over the users keeps
 * each user far below the gateway's per-user rate limit (50 requests/s), so the limiter is not what a run measures,
 * and each VU renews only its own user's token.
 */
export function walletOfVu(wallets) {
  return wallets[(exec.vu.idInTest - 1) % wallets.length];
}

/** A random wallet other than `from` (all wallets share CURRENCY). */
export function randomOtherWallet(wallets, from) {
  for (;;) {
    const to = wallets[Math.floor(Math.random() * wallets.length)];
    if (to.id !== from.id) return to;
  }
}

/** A random integer amount in minor units, between min and max inclusive. */
export function randomAmountMinor(min, max) {
  return min + Math.floor(Math.random() * (max - min + 1));
}

/**
 * handleSummary() body for every scenario. setup() returns the users' tokens (and JWTs carry the user's email), and
 * k6 writes setup data into the --summary-export JSON, so it is removed here. Returning undefined keeps k6's default
 * end-of-test summary on stdout (measured with k6 2.4.0); the export is written after this function runs.
 */
export function summaryWithoutSetupData(data) {
  delete data.setup_data;
  return undefined;
}
