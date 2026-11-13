# ADR 0001: Microservices for this project, knowing a modular monolith fits better

Date: 2026-11-13. Status: accepted.

## Context

The domain is small: four business capabilities, one developer, no independent teams,
no divergent scaling needs. By the usual criteria this is a modular monolith. The
owner runs a modular monolith in production and chose it on purpose.

The purpose of this repository is different from the purpose of a product. It must
show working evidence of distributed-system patterns: asynchronous messaging, outbox
and inbox, sagas with compensation, per-service data, Kubernetes deployment.

## Decision

Build it as microservices: Accounts and Ledger, Payments, Risk, Notifications, a
gateway, and (Phase 2) Reporting and Reconciliation. One repository, one solution.

Keep the split honest:

- Each service owns its database. No shared tables, no cross-database reads.
- Services communicate only by messages. No synchronous service-to-service HTTP.
- The ledger invariant stays local to one service and one transaction.
- `docs/when-monolith.md` states plainly what a monolith version would look like and
  when it would be the better choice. The README links to it from the first screen.

## Consequences

- Much more infrastructure and failure handling than the domain needs. That cost is
  the content of the project and is measured, not hidden.
- A transfer needs a broker round trip where a monolith needs one function call.
  k6 results will show that latency cost.
- Shared code lives in `BuildingBlocks` and `Contracts`. In a monorepo these are
  project references. With separate repositories they would be versioned packages.

## Alternatives considered

- **Modular monolith with in-process messaging.** Right for a product, wrong for the
  evidence goal: no broker failure, no network partition, no deployment topology.
- **Two services only.** This is M1 and is a complete, presentable stage on its own.
