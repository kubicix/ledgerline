# ADR 0005: Docker Compose is the one command, Aspire is not used

Date: 2026-11-13. Status: accepted. Versions: ADR 0000.

## Context

The project must start with one command. Candidates: Docker Compose (v5.6 locally) or
Aspire (13.6.5), which orchestrates projects and containers from a C# AppHost, ships a
dashboard, and can publish Compose files and (per its release notes) Kubernetes
manifests.

## Decision

Docker Compose.

- Infrastructure only: `docker compose up -d`
- Whole system: `docker compose --profile app up --build -d`

Reasons:

1. **Anyone can run it.** A reviewer needs Docker and nothing else. Aspire needs the
   .NET SDK and the Aspire tooling on the host.
2. **Failure tests need container-level control.** Killing a service with `SIGKILL`
   and stopping the broker are plain Docker operations against Compose services.
3. **The path to Kubernetes stays explicit.** Dockerfiles, environment variables,
   health checks, and migration jobs are written by hand once and reused by Helm. The
   project wants to show that work, not generate it.
4. **Stability.** Aspire shipped six minor releases in 2026. Its Kubernetes support
   was introduced as a preview in 13.3; its current status was not verified.

## Consequences

- No Aspire dashboard and no service discovery helpers. Observability comes from the
  Grafana LGTM container (ADR 0009). Service addresses are environment variables.
- The inner loop for one service is: `docker compose up -d` for infrastructure, then
  `dotnet run` for the service. `appsettings.Development.json` points at localhost.
- Compose profiles: default (infrastructure), `app` (services and gateway), `phase2`
  (Kafka, MongoDB, Reporting, Reconciliation), `ui`.

## Revisit when

A second developer joins, or the inner loop needs more than one service running from
source most of the time. Adding an Aspire AppHost later is additive and does not
change the services.
