# Ledgerline

A payment and wallet backend built as .NET 10 microservices: an event-sourced
double-entry ledger, payment sagas with compensation, a hand-rolled outbox and inbox
on RabbitMQ, and tests that prove no money is lost when things fail.

> **What this is and is not.** A portfolio project that demonstrates concepts:
> idempotency, double-entry bookkeeping, sagas, reconciliation. It moves no real
> money, talks to a fake bank that lives in this repository, is not PCI DSS
> compliant, and is not banking experience. At this size I would build it as a
> modular monolith; see [When would I build this as a monolith?](docs/when-monolith.md)

## Status

| Milestone | Scope | State |
|---|---|---|
| Design | Design, specs, ADRs, plan, walking skeleton | Done |
| M1 | Accounts and Ledger, Payments, RabbitMQ, outbox and inbox, one command | Not started |
| M2 | Sagas with compensation, Risk, Notifications, gateway, auth | Not started |
| M3 | Failure-scenario tests, k6, OpenTelemetry dashboards, CI security scans | Not started |
| M4 | Kubernetes with Helm and health checks | Not started |
| M5 | Kafka event stream, MongoDB read model, reconciliation job | Not started |
| M6 | Blazor UI | Not started |

Today the repository contains the documents and a walking skeleton: every project
exists and builds, the infrastructure starts, CI runs. There is no business logic yet.

## Quick start

Requirements: Docker with Compose v2. The .NET 10 SDK only if you want to build or
test from source.

```bash
# Today: infrastructure only (PostgreSQL, RabbitMQ, Redis, Keycloak, Grafana LGTM, Mailpit)
docker compose up -d

# From M1 on: the whole system
# docker compose --profile app up --build -d
# ./scripts/smoke.sh
```

| What | URL | Credentials (local development only) |
|---|---|---|
| API gateway (from M2) | http://localhost:8080 | Keycloak tokens |
| Keycloak | http://localhost:8081 | `admin` / `ledgerline_dev` |
| RabbitMQ management | http://localhost:15672 | `ledgerline` / `ledgerline_dev` |
| Grafana | http://localhost:3000 | anonymous |
| Mailpit | http://localhost:8025 | none |

Build and test from source:

```bash
dotnet build Ledgerline.slnx
dotnet test --solution Ledgerline.slnx
```

## Architecture

_Placeholder: container diagram image exported from `docs/design.md` (M2-T19)._

Short version:

- **Accounts and Ledger** is the only writer of money. Append-only event store,
  double-entry journal, balances as projections.
- **Payments** orchestrates each payment as a saga across Risk, the bank, and the
  ledger, and compensates when a later step fails.
- **Risk** applies limits and velocity checks. **Notifications** sends emails.
- Services own their databases and talk only through RabbitMQ. Every outgoing message
  goes through a transactional outbox; every consumer deduplicates through an inbox.
- **Gateway** (YARP) is the single entry point. **Keycloak** issues tokens.

Full design: [docs/design.md](docs/design.md). Per-service specs: [docs/specs](docs/specs).

## Decisions

| ADR | Decision |
|---|---|
| [0000](docs/adr/0000-verified-versions.md) | Versions and licenses, verified against official sources |
| [0001](docs/adr/0001-microservices-vs-modular-monolith.md) | Microservices here, knowing a modular monolith fits better |
| [0002](docs/adr/0002-messaging-library.md) | Raw RabbitMQ client, no bus library |
| [0003](docs/adr/0003-outbox-inbox.md) | Transactional outbox with a polling relay, inbox table |
| [0004](docs/adr/0004-identity-provider.md) | Keycloak |
| [0005](docs/adr/0005-compose-vs-aspire.md) | Docker Compose as the one command |
| [0006](docs/adr/0006-saga-orchestration.md) | Orchestrated saga, hand-written state machine |
| [0007](docs/adr/0007-event-sourcing-scope.md) | Event sourcing for the ledger only |
| [0008](docs/adr/0008-test-strategy.md) | Test strategy |
| [0009](docs/adr/0009-observability.md) | OpenTelemetry to Grafana LGTM |
| [0010](docs/adr/0010-scheduler.md) | Quartz.NET for reconciliation |
| [0011](docs/adr/0011-kubernetes-packaging.md) | kind, one Helm chart, dev-grade infrastructure |

## Failure scenarios

_Placeholder: filled in M3-T13 with links to the test files._

| Scenario | Claim | Test | Level | Result |
|---|---|---|---|---|
| Broker down | Requests are accepted, nothing is lost, everything completes after recovery | TBD | integration, system | TBD |
| Service crashes mid-flow | No lost or duplicated money; the flow finishes after restart | TBD | integration, system | TBD |
| Message delivered twice | No second effect | TBD | integration, system | TBD |
| Concurrent transfers on one account | No overdraft, no deadlock | TBD | integration | TBD |

How the tests work: [docs/testing.md](docs/testing.md).

## Measurements

_Placeholder: filled in M3-T10. Numbers will come with hardware, commit, and misses._

| Scenario | Load | p95 accept | p95 saga completion | Errors | Target met |
|---|---|---|---|---|---|
| Transfers, steady | TBD | TBD | TBD | TBD | TBD |
| Top-ups, steady | TBD | TBD | TBD | TBD | TBD |
| Hot account | TBD | TBD | TBD | TBD | TBD |
| Spike | TBD | TBD | TBD | TBD | TBD |

_Placeholder: Grafana dashboard screenshot (`docs/results/img/dashboard.png`)._

_Placeholder: one top-up trace across five services (`docs/results/img/trace-topup.png`)._

## Run on Kubernetes

_Placeholder: filled in M4-T08 (kind, Helm, probes, limits of the dev-grade setup)._

## Phase 2: event stream, read model, reconciliation

_Placeholder: filled in M5-T11._

## Demo UI

_Placeholder: filled in M6-T06._

## Security scans in CI

_Placeholder: filled in M3-T13 (dependency review, vulnerable packages, secret scan,
static analysis, image scan)._

## When would I build this as a monolith?

Almost always, at this size. The reasoning, the cost table, and the order in which I
would split a monolith are in [docs/when-monolith.md](docs/when-monolith.md).

## What I would do differently in production

_Placeholder: filled as milestones complete. Known items so far:_

- Use a maintained messaging library instead of a hand-rolled layer (ADR 0002).
- Separate database instances per service, managed or run by operators (ADR 0011).
- A hardened identity provider setup instead of Keycloak dev mode (ADR 0004).
- An external secret store.
- Distributed rate limiting at the gateway.

## Repository layout

```
docs/         design, specs, ADRs, testing, plan
src/          BuildingBlocks, Contracts, Services, Gateway
tests/        unit, integration, architecture, system tests; k6 scripts (M3)
deploy/       Compose support files; Helm chart (M4)
```

## Implementation plan

[docs/plan.md](docs/plan.md): six milestones, small test-first tasks, each with the
command that proves it is done.

## License

_Placeholder: to be chosen by the owner before the repository is made public._
