# Spec: Risk

Project `src/Services/Risk/Ledgerline.Risk`. Database `risk_db`. Redis for velocity
windows. Shared conventions: `building-blocks.md`.

## 1. Responsibilities

- Evaluate each payment against configurable rules and answer approve or reject.
- Keep the counters the rules need.
- Let an admin read and change rules and inspect decisions.

Not responsible for: fraud detection of any real kind. The rules are simple limits and
exist to give the saga a step that can say no and a service that can be down.

## 2. Rules

Evaluated in this order. All failing rules are reported, not only the first.

| Code | Meaning | Parameters | Default seed |
|---|---|---|---|
| `MaxSingleAmount` | One payment may not exceed a threshold | `kind` (nullable = all), `currency`, `thresholdMinor` | 5 000 000 minor units per currency, all kinds |
| `DailyAmountLimit` | Sum of approved amounts per customer, currency, and UTC day may not exceed a threshold | `currency`, `thresholdMinor` | 20 000 000 minor units |
| `VelocityLimit` | At most `maxCount` evaluations per customer within `windowSeconds` | `maxCount`, `windowSeconds` | 10 per 60 s |

Semantics:

- `DailyAmountLimit` counts **approved attempts**, whether or not the payment later
  completes (design assumption 8).
- `VelocityLimit` counts every evaluation, approved or not.
- A disabled rule is skipped. No enabled rules means approve.

## 3. Messages

Exchange `ledgerline.risk`. Queue `risk.inbox`. Records in `Ledgerline.Contracts.Risk`.

### Consumed: `risk.command.evaluate-risk.v1`

```json
{ "paymentId": "...", "kind": "TopUp", "customerId": "...", "accountId": "...",
  "amountMinor": 150000, "currency": "TRY", "requestedAt": "2026-11-13T10:15:30Z" }
```

### Published

`risk.event.risk-approved.v1`

```json
{ "paymentId": "...", "evaluatedAt": "2026-11-13T10:15:30.350Z" }
```

`risk.event.risk-rejected.v1`

```json
{ "paymentId": "...", "ruleCodes": ["DailyAmountLimit"], "evaluatedAt": "2026-11-13T10:15:30.350Z" }
```

## 4. Evaluation algorithm

Inside the consumer pipeline's transaction:

1. Look up `risk_decisions` by `paymentId`. Found: publish the stored decision again
   and return. This makes a saga re-send safe.
2. Load enabled rules (cached in memory for 10 s).
3. `VelocityLimit`: Redis sorted set `risk:velocity:<customerId>`, member `paymentId`,
   score `requestedAt` in milliseconds. One Lua script removes members older than the
   window, adds the member, sets the key TTL to the window, and returns the count.
   Adding the same `paymentId` twice does not increase the count.
   **Redis unavailable:** fall back to
   `SELECT count(*) FROM risk_decisions WHERE customer_id = @c AND evaluated_at > now() - window`
   plus one for the current evaluation. Slower, still correct.
4. `MaxSingleAmount`: compare.
5. `DailyAmountLimit`: `SELECT ... FROM daily_totals WHERE (customer_id, day, currency) = ... FOR UPDATE`
   (insert the row first with `ON CONFLICT DO NOTHING`). Reject if
   `total_minor + amountMinor > thresholdMinor`.
6. Approved: add the amount to `daily_totals`. Insert `risk_decisions`. Enqueue the
   reply. Commit.

The day is `requestedAt` in UTC.

## 5. HTTP API (admin only)

Through the gateway under `/api/risk/...`.

| Method and path | Body or result |
|---|---|
| `GET /risk/rules` | `{ "items": [ { "id", "code", "kind", "currency", "thresholdMinor", "maxCount", "windowSeconds", "enabled" } ] }` |
| `PUT /risk/rules/{id}` | `{ "thresholdMinor", "maxCount", "windowSeconds", "enabled" }`. Fields that do not apply to the rule's code must be null. `200` with the rule |
| `GET /risk/decisions/{paymentId}` | `{ "paymentId", "customerId", "outcome": "Approved|Rejected", "ruleCodes": [], "evaluatedAt" }` or `404` |

Rule changes take effect within 10 s (cache). Rules are not created or deleted through
the API; the seed defines the set.

## 6. Database schema (`risk_db`)

```sql
CREATE TABLE risk_rules (
    id              uuid PRIMARY KEY,
    code            text    NOT NULL,          -- MaxSingleAmount | DailyAmountLimit | VelocityLimit
    kind            text    NULL,
    currency        char(3) NULL,
    threshold_minor bigint  NULL,
    max_count       int     NULL,
    window_seconds  int     NULL,
    enabled         boolean NOT NULL DEFAULT true,
    updated_at      timestamptz NOT NULL
);

CREATE TABLE risk_decisions (
    payment_id   uuid PRIMARY KEY,
    customer_id  uuid        NOT NULL,
    kind         text        NOT NULL,
    amount_minor bigint      NOT NULL,
    currency     char(3)     NOT NULL,
    outcome      text        NOT NULL,
    rule_codes   text[]      NOT NULL DEFAULT '{}',
    evaluated_at timestamptz NOT NULL
);
CREATE INDEX ix_decisions_customer_time ON risk_decisions (customer_id, evaluated_at DESC);

CREATE TABLE daily_totals (
    customer_id uuid    NOT NULL,
    day         date    NOT NULL,
    currency    char(3) NOT NULL,
    total_minor bigint  NOT NULL DEFAULT 0,
    PRIMARY KEY (customer_id, day, currency)
);

-- plus outbox_messages and inbox_messages
```

## 7. Invariants

| # | Invariant | Enforced by |
|---|---|---|
| R1 | One decision per `paymentId`, and every reply for it is identical | `risk_decisions` primary key, step 1 |
| R2 | `daily_totals.total_minor` equals the sum of approved `risk_decisions` for that key | Same transaction; integration test recomputes |
| R3 | Two concurrent evaluations for one customer cannot both pass a daily limit that only one fits under | Row lock on `daily_totals` |
| R4 | A Redis outage never approves something the rules would reject | Database fallback |

## 8. Failure handling

| Failure | Behavior |
|---|---|
| Risk down | Commands wait in `risk.inbox`. Payments re-sends on timeout; decisions are idempotent |
| Redis down | Fallback query; warning log; counter `ledgerline.risk.redis_fallback` |
| Redis lost its data | Velocity windows restart empty: for at most one window the rule is more permissive. Accepted and documented; the daily limit is unaffected |
| Rule misconfiguration (null threshold on an enabled rule) | The rule is skipped and an error is logged; `PUT` validation prevents creating this state |
