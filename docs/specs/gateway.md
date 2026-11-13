# Spec: Gateway

Project `src/Gateway/Ledgerline.Gateway`. YARP 2.3.0. No database. Redis for rate
limiting state is **not** used; limits are per instance (stated limitation).

## 1. Responsibilities

- Single public entry point on port 8080.
- Validate JWTs (issuer, audience `ledgerline-api`, lifetime, signature via Keycloak's
  JWKS) and reject unauthenticated calls before they reach a service.
- Coarse role policies per route.
- Rate limiting, request size limit, correlation ID, forwarded headers.
- Start the server span of every trace.

Not responsible for: business authorization (account ownership is checked by the
owning service), response aggregation, caching, idempotency handling (the header is
passed through; services enforce it).

## 2. Routes

| Public path | Cluster | Upstream path | Policy |
|---|---|---|---|
| `/api/accounts/{**rest}` | accounts | `/accounts/{**rest}` | authenticated |
| `/api/ledger/{**rest}` | accounts | `/ledger/{**rest}` | `Admin` |
| `/api/topups/{**rest}` | payments | `/topups/{**rest}` | `Customer` |
| `/api/transfers/{**rest}` | payments | `/transfers/{**rest}` | `Customer` |
| `/api/payments/{**rest}` | payments | `/payments/{**rest}` | authenticated |
| `/api/risk/{**rest}` | risk | `/risk/{**rest}` | `Admin` |
| `/api/notifications/{**rest}` | notifications | `/notifications/{**rest}` | authenticated |
| `/api/reporting/{**rest}` (Phase 2) | reporting | `/{**rest}` | authenticated |
| `/api/reconciliation/{**rest}` (Phase 2) | reconciliation | `/{**rest}` | `Admin` |

Not routed: `/health/*` of services, Bank Simulator, Keycloak.

Cluster addresses come from configuration `ReverseProxy:Clusters:<name>:Destinations`
and are set by environment variables in Compose and Helm. Active health checks call
each destination's `/health/ready` every 10 s.

## 3. Cross-cutting behavior

| Concern | Rule |
|---|---|
| Correlation | If `X-Correlation-Id` is absent or not a UUID, generate a UUIDv7. Forward it and return it on the response |
| Authorization header | Forwarded unchanged. Services validate the token again |
| Rate limit | Token bucket per `sub` (or per client IP when anonymous): 50 requests per second, burst 100. Excess: `429 rate-limited` with `Retry-After` |
| Body size | 64 KB maximum. Larger: `413` |
| Timeouts | 10 s per upstream request. Timeout: `504` problem details |
| Retries | None. POSTs must not be retried by the gateway; clients retry with the same `Idempotency-Key` |
| Errors | Upstream problem details pass through. Gateway-generated errors use the same format |
| CORS | Allowed origin from configuration (`http://localhost:5100` for the UI) |

## 4. Health

`/health/live` and `/health/ready`. Ready when the JWKS has been fetched at least
once. The gateway stays ready when a downstream cluster is unhealthy; it returns `503`
for that route only.

## 5. Failure handling

| Failure | Behavior |
|---|---|
| Keycloak down | Already cached signing keys keep working until they rotate. New instances are not ready |
| A service is down | `503` for its routes; other routes unaffected |
| Gateway instance restarts | Rate limit counters reset (per-instance limitation) |

## 6. Stated limitations

- Per-instance rate limiting. With N replicas the effective limit is N times higher.
  A Redis-backed limiter is the fix and is listed as possible future work.
- No mTLS between gateway and services. In Kubernetes, services are ClusterIP-only
  and still validate the JWT.
