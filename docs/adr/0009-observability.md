# ADR 0009: OpenTelemetry to Grafana LGTM

Date: 2026-11-13. Status: accepted. Versions: ADR 0000.

## Decision

- All services use the OpenTelemetry .NET SDK (1.21.0) for traces, metrics, and logs,
  exported over OTLP. Setup lives once in `Ledgerline.BuildingBlocks.Web`.
- The local backend is `grafana/otel-lgtm`: one container with an OpenTelemetry
  Collector, Tempo, Loki, Prometheus, and Grafana.
- Dashboards are JSON files in `deploy/observability/dashboards/`, provisioned into
  Grafana at startup, so the README screenshots are reproducible.

## Why not the Aspire dashboard

It is excellent for the inner loop but keeps data in memory and has no saved
dashboards. The project needs a dashboard that can be versioned and a screenshot that
can be reproduced. LGTM also shows the stack many teams actually run.

## Trace propagation through messaging

A trace must not end at the outbox.

- When an outbox row is created, the current `traceparent` is stored in its headers.
- The relay starts a `PRODUCER` span linked to that context and writes `traceparent`
  into the AMQP headers.
- The consumer starts a `CONSUMER` span as a child of the incoming context.
- Result: one trace from HTTP request to the last saga reply. This is the trace
  screenshot for the README.

Span and attribute names follow the OpenTelemetry messaging semantic conventions
(`messaging.system = rabbitmq`, `messaging.destination.name`,
`messaging.message.id`). The conventions were not re-read for this ADR; check their
current status at M3.

## Custom metrics (meter `Ledgerline`)

| Metric | Type | Why |
|---|---|---|
| `ledgerline.outbox.lag` | histogram, seconds | Time from commit to confirmed publish |
| `ledgerline.outbox.pending` | gauge | Backlog; rises when the broker is down |
| `ledgerline.inbox.duplicates` | counter | Proof that duplicates happen and are absorbed |
| `ledgerline.consumer.retries`, `.dead_lettered` | counters | Poison message visibility |
| `ledgerline.saga.duration` | histogram, seconds, by kind and outcome | End-to-end payment time |
| `ledgerline.saga.manual_review` | counter | Should stay at zero; alert otherwise |
| `ledgerline.ledger.posting.duration` | histogram | Lock contention on hot accounts |
| `ledgerline.ledger.imbalance` | gauge | Sum of balances per currency; must be zero |

## Consequences

- LGTM is a dev image, not a production deployment. Stated in the README.
- Logs are structured (JSON to console and OTLP) and carry `trace_id`, `payment_id`,
  and `correlation_id`. Personal data (email) is never logged.
- Sampling is 100% locally. A production note in the README mentions tail sampling.
