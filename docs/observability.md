# Observability

All six services export traces, metrics, and logs over OTLP to `grafana/otel-lgtm`
(ADR 0009). The image bundles an OpenTelemetry Collector, Tempo, Loki, Prometheus,
and Grafana in one container. It is a development image, not a production
deployment.

## Open the dashboard

1. Start the stack: `docker compose --profile app up -d --build --wait`.
2. Generate some traffic, for example `./scripts/smoke.sh`.
3. Open <http://localhost:3000/d/ledgerline-overview> (Grafana allows anonymous
   access locally, no login). It is also listed under Dashboards as
   "Ledgerline Overview".

The dashboard is a file, `deploy/observability/dashboards/ledgerline-overview.json`,
provisioned at startup by `deploy/observability/provisioning/dashboards.yaml`.
`compose.yaml` mounts both into the `otel-lgtm` container. Edits made in the Grafana
UI are not saved back: change the JSON and Grafana reloads it within about 10 seconds
(`updateIntervalSeconds` in the provisioning file), no restart needed.

Counter panels (inbox duplicates, retries, dead letters, manual review) show no data
or 0 until the first event, because a counter is only exported after its first
increment.

`./scripts/check-dashboard.sh` starts the stack, runs the smoke script, and checks
that Grafana lists exactly one Ledgerline dashboard and that Prometheus holds the
outbox lag histogram. It stops the stack at the end.

## Find a trace by payment ID

The payment ID is also the saga correlation ID, and every messaging span carries it
as the attribute `messaging.message.conversation_id`.

1. Take the payment ID from the `id` field of the `POST` response, or from
   `GET /api/payments/{id}`.
2. In Grafana open Explore, pick the Tempo data source, choose the TraceQL tab, and
   run `{ span.messaging.message.conversation_id = "<payment id>" }`.
3. Open the returned trace. It starts at the gateway request and continues through
   the outbox publish and consume spans of the services that handle the payment.

HTTP spans carry the ID only in `url.path` when a client reads the payment
(`{ span.url.path = "/api/payments/<payment id>" }` finds those requests).
