# ADR 0004: Keycloak as the identity provider

Date: 2026-11-13. Status: accepted. Versions: ADR 0000.

## Context

The system needs OAuth2 and OpenID Connect: login for the UI, JWT access tokens for
the API, roles (`customer`, `merchant`, `admin`). Options: Keycloak (separate server,
26.8.2) or OpenIddict (library, 7.8.0, Apache-2.0) hosted in an ASP.NET Core service
of our own.

## Decision

Keycloak, run as a container with a realm imported from
`deploy/compose/keycloak/ledgerline-realm.json`.

Reasons:

- Identity is not what this project demonstrates. OpenIddict would mean writing and
  securing an identity service (user store, login UI, consent, key rotation).
- Keycloak is what the services would meet in many companies: an external issuer they
  only validate tokens from. That is the realistic integration to show.
- The realm file makes the whole setup reproducible with one command and reviewable
  in Git.

## Details

- Realm `ledgerline`. Realm roles `customer`, `merchant`, `admin`.
- Clients: `ledgerline-web` (public, authorization code with PKCE), `ledgerline-api`
  (bearer-only audience), `ledgerline-k6` (dev realm only, direct access grants, for
  load tests and system tests).
- Access tokens carry `aud: ledgerline-api`, `sub`, `email`, and realm roles.
- The gateway validates tokens and applies coarse route policies. Each service
  validates the token again and applies resource-level checks (account ownership).
- Seeded users (`alice`, `bob` as customers, `shop` as merchant, `admin`) exist only
  in the dev realm. Their passwords are development values in the realm file.

## Consequences

- Keycloak is the heaviest container in the stack (startup time, memory).
- Dev mode (`start-dev`, embedded H2, HTTP) is used everywhere in this repository.
  Production hardening of Keycloak is a non-goal and the README says so.
- Keycloak has no LTS. The image tag is pinned and re-verified at each milestone.
- Integration tests of single services do not start Keycloak. They use locally signed
  test tokens. Only system tests and k6 use the real provider.

## Alternatives considered

- **OpenIddict:** better when the identity server must live inside a .NET codebase and
  be customized deeply. Not needed here.
- **Duende IdentityServer:** commercial license for most uses. Not considered further.
