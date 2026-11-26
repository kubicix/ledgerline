// Keycloak tokens for the seeded realm users, through the development-only public client `ledgerline-k6`
// (password grant, like scripts/smoke.sh and tests/Ledgerline.SystemTests/Harness/KeycloakTokens.cs).
// Tokens and user details are never printed.
import http from 'k6/http';

export const KEYCLOAK_URL = __ENV.KEYCLOAK_URL || 'http://keycloak:8081/realms/ledgerline';
const CLIENT_ID = 'ledgerline-k6';
// Renew this long before expiry. The realm keeps the Keycloak default access token lifespan (300 s), shorter than a
// 5 minute scenario, so long runs must renew.
const RENEW_BEFORE_MS = 30000;

const list = (value) => value.split(',').map((x) => x.trim()).filter((x) => x.length > 0);
const numbered = (prefix, count, width) =>
  Array.from({ length: count }, (_, i) => `${prefix}${String(i + 1).padStart(width, '0')}`);

// The user pool is data. Default: the load-test users of the realm import, load-customer-001 to load-customer-200 and
// load-merchant-01 to load-merchant-10 (testing.md section 5: 200 customer and 10 merchant wallets). Override with
// K6_CUSTOMERS, K6_MERCHANTS and K6_ADMIN (comma separated usernames), for example K6_CUSTOMERS=alice,bob.
export const USERS = {
  customers: __ENV.K6_CUSTOMERS ? list(__ENV.K6_CUSTOMERS) : numbered('load-customer-', 200, 3),
  merchants: __ENV.K6_MERCHANTS ? list(__ENV.K6_MERCHANTS) : numbered('load-merchant-', 10, 2),
  admin: __ENV.K6_ADMIN || 'admin',
};

// Development passwords from deploy/compose/keycloak/ledgerline-realm.json; overridable like scripts/smoke.sh
// (ALICE_PASSWORD and so on).
function passwordOf(username) {
  return __ENV[`${username.toUpperCase().replace(/[^A-Z0-9]/g, '_')}_PASSWORD`] || `${username}_dev`;
}

const TOKEN_URL = `${KEYCLOAK_URL}/protocol/openid-connect/token`;
const TOKEN_PARAMS = { tags: { name: 'POST keycloak token', op: 'token' } };

function read(res, username) {
  if (res.status !== 200) {
    // The username is a seeded login name, not an email; the body is not printed because it may echo input.
    throw new Error(`Keycloak token request for ${username} failed with ${res.status}`);
  }
  const body = res.json();
  return {
    accessToken: body.access_token,
    refreshToken: body.refresh_token,
    expiresAt: Date.now() + body.expires_in * 1000,
  };
}

function grant(form, username) {
  return read(http.post(TOKEN_URL, form, TOKEN_PARAMS), username);
}

const passwordForm = (username) => ({ grant_type: 'password', client_id: CLIENT_ID, username, password: passwordOf(username) });

/** Fetches a fresh token with the password grant. */
export function login(username) {
  return grant(passwordForm(username), username);
}

// Per VU: every VU has its own JavaScript runtime, so this cache is per VU.
const cache = {};
let setupTokens = {};

/** Lets this VU use the tokens fetched in setup(), so VUs do not each run a password grant. */
export function seedTokens(tokens) {
  setupTokens = tokens || {};
}

/**
 * Returns a valid access token for the user. Renews 30 s before expiry with the refresh token (cheap, no password
 * hashing in Keycloak) and falls back to the password grant when the refresh is refused.
 */
export function tokenFor(username) {
  if (!cache[username] && setupTokens[username]) cache[username] = setupTokens[username];
  const cached = cache[username];
  if (cached && cached.expiresAt - RENEW_BEFORE_MS > Date.now()) return cached.accessToken;
  let fresh = null;
  if (cached && cached.refreshToken) {
    try {
      fresh = grant({ grant_type: 'refresh_token', client_id: CLIENT_ID, refresh_token: cached.refreshToken }, username);
    } catch (error) {
      fresh = null;
    }
  }
  cache[username] = fresh || login(username);
  return cache[username].accessToken;
}

// Password grants are CPU heavy in Keycloak; setup sends this many at a time.
const LOGIN_BATCH = 10;

/** Password-grant tokens for every user in the pool, to hand from setup() to the VUs. */
export function loginAll() {
  const users = [...USERS.customers, ...USERS.merchants, USERS.admin];
  const tokens = {};
  for (let i = 0; i < users.length; i += LOGIN_BATCH) {
    const chunk = users.slice(i, i + LOGIN_BATCH);
    const responses = http.batch(chunk.map((username) => ['POST', TOKEN_URL, passwordForm(username), TOKEN_PARAMS]));
    chunk.forEach((username, j) => { tokens[username] = read(responses[j], username); });
  }
  return tokens;
}
