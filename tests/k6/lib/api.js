// Calls through the YARP gateway. Routes and payloads mirror scripts/smoke.sh and
// tests/Ledgerline.SystemTests/Harness/LedgerlineClient.cs. Money is integer minor units.
import http from 'k6/http';
import { sleep } from 'k6';
import { tokenFor } from './auth.js';

export const GATEWAY_URL = __ENV.GATEWAY_URL || 'http://gateway:8080';

// 404 is expected only while Payments has not yet received the AccountOpened replica of a new wallet (setup retries it).
const replicaLagExpected = http.expectedStatuses({ min: 200, max: 399 }, 404);

function headers(user, idempotencyKey) {
  const result = { Authorization: `Bearer ${tokenFor(user)}`, 'Content-Type': 'application/json' };
  if (idempotencyKey) result['Idempotency-Key'] = idempotencyKey;
  return result;
}

export function get(user, path, name, extraTags) {
  return http.get(`${GATEWAY_URL}${path}`, {
    headers: headers(user), tags: Object.assign({ name: `GET ${name || path}`, op: 'read' }, extraTags || {}),
  });
}

export function post(user, path, body, idempotencyKey, name, extraTags, params) {
  return http.post(`${GATEWAY_URL}${path}`, JSON.stringify(body), Object.assign({
    headers: headers(user, idempotencyKey), tags: Object.assign({ name: `POST ${name || path}`, op: 'write' }, extraTags || {}),
  }, params || {}));
}

export function put(user, path, body, name) {
  return http.put(`${GATEWAY_URL}${path}`, JSON.stringify(body), {
    headers: headers(user), tags: { name: `PUT ${name || path}`, op: 'write' },
  });
}

/** POST for setup: retries with the same key while the wallet replica is missing (404) or the gateway is busy. */
export function postWithRetry(user, path, body, idempotencyKey, name, timeoutMs) {
  const deadline = Date.now() + (timeoutMs || 30000);
  for (;;) {
    const res = post(user, path, body, idempotencyKey, name, { op: 'setup' }, { responseCallback: replicaLagExpected });
    const transient = res.status === 404 || res.status === 429 || res.status >= 502 ||
      (res.status === 409 && String(res.body).includes('request-in-progress'));
    if (!transient || Date.now() >= deadline) return res;
    sleep(0.2);
  }
}

export const listAccounts = (user) => get(user, '/api/accounts', '/api/accounts', { op: 'setup' });

export const openWallet = (user, type, currency, key) =>
  postWithRetry(user, '/api/accounts', { type, currency }, key, '/api/accounts');

export const getPayment = (user, id) => get(user, `/api/payments/${id}`, '/api/payments/{id}', { op: 'poll' });

export const listRefunds = (user, paymentId) =>
  get(user, `/api/payments/${paymentId}/refunds?limit=100`, '/api/payments/{id}/refunds', { op: 'poll' });

export const getTrialBalance =(admin) => get(admin, '/api/ledger/trial-balance', '/api/ledger/trial-balance', { op: 'check' });

export function topUpBody(accountId, amountMinor, currency) {
  return { accountId, amountMinor, currency, cardToken: 'tok_ok' };
}

export function transferBody(fromAccountId, toAccountId, amountMinor, currency) {
  return { fromAccountId, toAccountId, amountMinor, currency };
}

/** POST /api/payments by the customer: authorizes (Manual) or authorizes and captures (Automatic) a merchant payment. */
export function merchantPaymentBody(customerAccountId, merchantAccountId, amountMinor, currency, captureMode) {
  return { customerAccountId, merchantAccountId, amountMinor, currency, captureMode };
}
