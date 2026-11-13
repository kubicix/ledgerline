# ADR 0011: Kubernetes on kind with one Helm chart, dev-grade infrastructure

Date: 2026-11-13. Status: accepted for M4. Versions: ADR 0000.

## Decision

- **Cluster:** kind (v0.33.0). It runs in Docker, which is already required, and has
  an official GitHub Action for CI. k3s was the alternative; it is closer to a real
  node but harder to run on Windows.
- **Packaging:** one umbrella Helm chart at `deploy/helm/ledgerline` (Helm v4).
  - `templates/service/*`: one generic set of templates (Deployment, Service,
    ConfigMap, migration Job, PodDisruptionBudget) rendered once per entry in
    `values.yaml: services`. Five near-identical services do not need five charts.
  - `templates/infra/*`: PostgreSQL, RabbitMQ, Redis, Keycloak, LGTM, Mailpit as
    plain StatefulSets or Deployments using the same official images as Compose.
    Each can be disabled with `infra.<name>.enabled=false` to point at external ones.
- **No Bitnami charts.** Bitnami changed the terms of its free catalog in 2025. To
  avoid depending on that, infrastructure manifests are written by hand. Current
  Bitnami terms were not re-checked.
- **Health:** every service has startup, liveness (`/health/live`), and readiness
  (`/health/ready`) probes. Readiness checks only the service's own database and the
  broker connection.
- **Migrations:** a Helm `pre-install,pre-upgrade` hook Job per service runs the
  service image with the `migrate` argument.
- **Graceful shutdown:** services stop consuming, finish in-flight handlers, and let
  the relay finish its batch within `terminationGracePeriodSeconds: 30`.
- **Ingress:** the gateway is exposed with a NodePort mapped by the kind config to
  `localhost:8080`. No ingress controller.
- **Config and secrets:** ConfigMap for non-secret settings, Secret for connection
  strings, both from Helm values. Dev values are committed; a real deployment would
  use an external secret store.

## Consequences

- The infrastructure on Kubernetes is single-replica and not production-grade. The
  README names the production path: CloudNativePG, the RabbitMQ Cluster Operator, and
  a managed Redis or Valkey.
- Proof for the definition of done: a CI job creates a kind cluster, installs the
  chart, waits for readiness, and runs a smoke test (top-up and transfer) through the
  gateway. A second step deletes a Payments pod mid-run and asserts the flow finishes.
