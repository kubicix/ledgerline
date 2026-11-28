# ADR 0000: Verified versions and licenses

Date: 2026-11-13. Status: accepted. Re-verify at the start of every milestone.

## Why this exists

Versions and licenses in this project are taken from official sources, not from memory.
This file records what was checked, where, and what could not be checked. Every other
ADR refers to it instead of repeating numbers.

## How it was verified

- NuGet: `https://api.nuget.org/v3-flatcontainer/<id>/index.json` and the package
  `.nuspec` (license expression, target frameworks).
- Container images: Docker Hub and Quay tag APIs.
- GitHub Actions and CLIs: GitHub releases API (`releases/latest`).
- MassTransit: `masstransit.io/introduction/v9-announcement` and the NuGet listing.
- Wolverine: NuGet, the `LICENSE` and `docs/guide/migration.md` in the GitHub repository.
- Keycloak: `keycloak.org` release announcements.

## .NET

| Item | Version | Note |
|---|---|---|
| .NET SDK (local) | 10.0.402 | `global.json` pins `10.0.400` with `rollForward: latestFeature` |
| Target framework | `net10.0` | LTS. .NET 11 packages (11.0.0) exist on NuGet; not used |
| Test runner | Microsoft.Testing.Platform | Required by xunit.v3 4.x on the .NET 10 SDK; enabled in `global.json` |

## NuGet packages

| Package | Version | License | Used for |
|---|---|---|---|
| RabbitMQ.Client | 7.2.2 | Apache-2.0 OR MPL-2.0 | Messaging transport |
| MailKit | 5.0.0 | MIT | SMTP sender. Verified on nuget.org/packages/MailKit/5.0.0 on 2026-11-13 |
| Microsoft.EntityFrameworkCore (and ASP.NET Core packages) | 10.0.15 | MIT | Persistence, web. Latest `10.0.x`; `11.0.0` is the newest overall |
| Microsoft.Extensions.Diagnostics.HealthChecks | 10.0.15 | MIT | Broker readiness check in `BuildingBlocks.Messaging` (M4-T02). Checked on nuget.org on 2026-11-26: 10.0.15 is the latest `10.0.x` |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL | EF Core provider |
| StackExchange.Redis | 3.4.35 | MIT | Idempotency cache, velocity windows, locks |
| Yarp.ReverseProxy | 2.3.0 | MIT | Gateway. Targets net8.0 at most; runs on net10.0 |
| OpenTelemetry.Extensions.Hosting | 1.21.0 | Apache-2.0 | Observability. Re-verified on nuget.org on 2026-11-25 (M3), with the four rows below |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.21.0 | Apache-2.0 | OTLP export of traces, metrics, logs |
| OpenTelemetry.Instrumentation.AspNetCore | 1.21.0 | Apache-2.0 | Versions independently of the SDK; 1.21.0 is the latest stable |
| OpenTelemetry.Instrumentation.Http | 1.21.0 | Apache-2.0 | Versions independently of the SDK; 1.21.0 is the latest stable |
| Npgsql.OpenTelemetry | 10.0.3 | PostgreSQL | Npgsql tracing. Depends on Npgsql 10.0.3, the version the solution resolves (10.0.4 exists and would pull Npgsql 10.0.4) |
| OpenTelemetry.Exporter.InMemory | 1.21.0 | Apache-2.0 | Tests only (`TracingTests`) |
| Microsoft.Extensions.Http.Resilience | 10.11.0 | MIT | Bank HTTP client retries and timeouts |
| xunit.v3 | 4.0.3 | Apache-2.0 | Tests |
| Shouldly | 4.3.0 | BSD-3-Clause | Assertions |
| Testcontainers (PostgreSql, RabbitMq, and others) | 4.16.0 | MIT | Integration and failure tests. All `Testcontainers.*` packages use one version; 4.17.0 exists and is not adopted here |
| Testcontainers.Kafka | 4.16.0 | MIT | Phase 2 test fixture (M5-T01). Re-verified on nuget.org on 2026-11-28: targets net10.0 and supports the `apache/kafka` image (KRaft, vendor detected from the image name). Same version as the other Testcontainers packages |
| Testcontainers.MongoDb | 4.16.0 | MIT | Phase 2 test fixture (M5-T01). Re-verified on nuget.org on 2026-11-28: targets net10.0. Same version as the other Testcontainers packages |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.15 | MIT | In-process service hosts in tests |
| Quartz | 4.7.3 | Apache-2.0 | Phase 2 reconciliation schedule |
| MongoDB.Driver | 3.14.1 | Apache-2.0 | Phase 2 read model. Re-verified on nuget.org on 2026-11-28 (M5-T01): 3.14.1 is the latest stable, a patch above the 3.14.0 first recorded. Ships net6.0 and netstandard2.1 assets, no net10.0 target of its own; both run on net10.0 |
| Confluent.Kafka | 2.16.0 | Apache-2.0 | Phase 2 event stream. Re-verified on nuget.org on 2026-11-28 (M5-T01): 2.16.0 is the latest stable, has a net10.0 asset, depends on librdkafka.redist 2.16.0. Producer settings checked against the package XML docs (M5-T02): `enable.idempotence=true` also sets `acks=all`, `max.in.flight<=5` and unbounded retries; `message.timeout.ms` (default 300000) is the local bound on one produce including retries, and `request.timeout.ms` is only enforced by the broker |

Considered and not used:

| Package | Version | License | Note |
|---|---|---|---|
| MassTransit 9 | 9.2.6 | Commercial (`massient.com/license`) | Requires a paid license |
| MassTransit 8 | 8.5.12 | Apache-2.0 | Official page promises patches "through at least the end of 2026". No exact end date is published |
| WolverineFx | 6.64.0 (7.0.0-alpha.1 exists) | MIT | Targets net9.0 and net10.0. EF Core outbox, inbox, and sagas |
| Marten | 9.59.0 | MIT | PostgreSQL event store |
| OpenIddict | 7.8.0 | Apache-2.0 | OIDC server library |
| Hangfire.Core | 1.8.25 | License file in package, not an SPDX expression. Commonly described as LGPL v3; **not verified** | Scheduler |
| Aspire.Hosting.AppHost | 13.6.5 | MIT (not verified from nuspec) | Orchestration |

## Container images

| Image | Tag | Note |
|---|---|---|
| postgres | 18.7-alpine | Data directory mount is `/var/lib/postgresql` from 18 on |
| rabbitmq | 4.4-management | Quorum queues are the default choice in 4.x |
| redis | 8.8-alpine | Redis 8 is tri-licensed: AGPLv3, RSALv2, SSPLv1. Used unmodified. Valkey 9.1 (BSD) is a drop-in alternative |
| quay.io/keycloak/keycloak | 26.8.2 | Released 2026-11-05. Keycloak has no LTS; only the newest release gets fixes |
| grafana/otel-lgtm | 0.41.0 | Dev-only all-in-one: Grafana, Tempo, Loki, Prometheus, OTel Collector |
| axllent/mailpit | v1.33.1 | Fake SMTP with a web UI |
| apache/kafka | 4.4.0 | Phase 2. KRaft mode, no ZooKeeper. Re-verified on Docker Hub on 2026-11-28 (M5-T01): 4.4.0 is the newest stable tag (4.3.2 and 4.2.2 are the older lines). Apache-2.0. Health check uses `/opt/kafka/bin/kafka-broker-api-versions.sh` |
| mongo | 9.0 (9.0.8) | Phase 2. Re-verified on Docker Hub on 2026-11-28 (M5-T01): 9.0.8 is the newest 9.x tag, so the 8.0 fallback is not needed. The server is SSPL-licensed; it is used unmodified as a dev and test container, the same position as Redis 8 above |
| zricethezav/gitleaks | v8.30.1 | Secret scan for local runs. Docker Hub tag checked 2026-11-26; the README also names `ghcr.io/gitleaks/gitleaks` |
| grafana/k6 | 2.4.0 | Load tests. Pulled on 2026-11-25: digest `sha256:d41bb79cd8de5a7ad619b53570e3712633e8bff0937010a5c6c57a4747e7a276`, `k6 version` prints `k6 v2.4.0 (commit/dd9395906d, go1.27.3, linux/amd64)`. Script API checked for M3-T08, see below |
| aquasec/trivy | 0.70.0 | Local image scan, same Trivy version as the CI action default. Docker Hub tag checked 2026-11-26 (no `v` prefix); pulled digest `sha256:be1190afcb28352bfddc4ddeb71470835d16462af68d310f9f4bca710961a41e`. Used instead of `:latest` |

## Tools and GitHub Actions

| Item | Version |
|---|---|
| Docker Engine / Compose (local) | 29.9.2 / v5.6.0 |
| kind | v0.33.0. Re-verified 2026-11-26: latest release (2026-08-26). Installed locally with winget (`Kubernetes.kind`). Default node image `kindest/node:v1.37.0`, not pinned in `deploy/kind/cluster.yaml` |
| Helm | v4.3.2. Re-verified 2026-11-26: latest v4 release (2026-11-13; v3 line is at v3.22.1). Installed locally with winget (`Helm.Helm`) |
| actions/checkout | v7 |
| actions/setup-dotnet | v6 |
| actions/upload-artifact | v7 |
| actions/dependency-review-action | v5. Re-checked 2026-11-26: latest release v5.0.1; `v5` is a branch, not a tag. Private repositories need GitHub Advanced Security (README) |
| gitleaks/gitleaks-action | v3. Re-checked 2026-11-26: tag `v3` (v3.0.0, Node 24). No license key needed for personal-account repositories, required for organizations (README). CI pins the CLI with `GITLEAKS_VERSION: 8.30.1`. On pull requests it calls `GET /repos/{owner}/{repo}/pulls/{pull_number}/commits` (v3 `src/gitleaks.js`; the README lists no permissions), so the job grants `pull-requests: read` |
| aquasecurity/trivy-action | v0.36.0. Verified 2026-11-26: tag `refs/tags/v0.36.0` exists and is the latest release (2026-04-22, GitHub releases API). Its `action.yaml` defaults the Trivy CLI to `v0.70.0` (installed through `aquasecurity/setup-trivy` v0.2.6, pinned by hash); CI passes `version: v0.70.0` explicitly. The newest Trivy release is v0.76.2 (2026-11-05); CI stays on the action default |
| helm/kind-action | v1.15.2. Re-verified 2026-11-26: latest release (2026-11-04) |
| azure/setup-helm | v5. Re-verified 2026-11-26: tag `refs/tags/v5` exists, latest release v5.1.0 (2026-11-09) |
| grafana/setup-k6-action | v1 |
| github/codeql-action | v4. Verified 2026-11-26: tag `refs/tags/v4` exists; newest tag v4.39.5 (GitHub tags API) |
| gitleaks CLI | v8.30.1. Latest release (GitHub releases API, 2026-11-26) |

## Specifications

| Item | Status | Note |
|---|---|---|
| OpenTelemetry messaging semantic conventions | Development (not stable), release v1.45.0 of 2026-11-02 | Checked in the `open-telemetry/semantic-conventions` repository on 2026-11-25. v1.45.0 renamed `messaging.system` to `messaging.system.name`. Ledgerline spans emit both, plus `messaging.destination.name` and `messaging.message.id` |

## k6 2.x script API (checked 2026-11-25 for M3-T08)

Sources: the k6 v2 migration guide (`grafana.com/docs/k6/latest/get-started/migrating-to-v2/`), the `latest` docs pages
for `k6/http` `setResponseCallback` and `expectedStatuses`, thresholds, `k6/metrics` `Trend` and the
`constant-arrival-rate` executor, and throwaway scripts run in `grafana/k6:2.4.0` itself.

- Unchanged in 2.x and used as documented: `k6/http` (`http.get`, `http.post`, `http.put`, per-request `tags` and
  `responseCallback`, `http.expectedStatuses`), `options.scenarios` with `constant-vus` and `constant-arrival-rate`
  (`rate`, `timeUnit`, `duration`, `preAllocatedVUs`, `maxVUs`, `exec`, scenario `tags`), `options.thresholds` with
  tagged sub-metrics (several tags in one selector, for example `http_req_duration{phase:load,op:accept}`, work), and
  `new Trend(name, true)` with `add(value, tags)`.
- Removed in 2.0: `--no-summary` (use `--summary-mode=disabled`), `--summary-mode=legacy`, the `externally-controlled`
  executor, `options.ext.loadimpact`, `k6/experimental/redis`. None is used here.
- `k6 run --summary-export <file>` still exists in 2.4.0 (`k6 run --help`). In the exported JSON a threshold value of
  `false` means the threshold passed.
- Measured in 2.4.0: a crossed threshold exits 99; an exception thrown in `teardown()` exits 107; a threshold on a tagged
  sub-metric with no samples passes (`p(95)` reads 0), so the scenarios pair each saga trend with a `count>0` guard;
  metrics added in `teardown()` count toward thresholds; requests in `setup()` and `teardown()` carry no scenario tags
  (only `group` `::setup` or `::teardown`); the `--summary-export` JSON includes the value `setup()` returned
  (`setup_data`) unless an exported `handleSummary(data)` deletes `data.setup_data`, which it does before the export
  is written; a `handleSummary` that returns `undefined` keeps the default end-of-test summary on stdout.
  `k6/summary` is not a built-in module.
- Checked 2026-11-26 for M3-T09 in `grafana/k6:2.4.0`:
  - `k6 inspect` exits 0 for `transfer-hot-account.js`, `payment-mix.js`, `spike.js` and `broker-outage.js`.
  - `ramping-arrival-rate` takes `startRate`, `timeUnit`, `stages` (`duration`, `target`), `preAllocatedVUs` and
    `maxVUs` as documented.
  - Two scenarios in one script, each with its own `exec` function, run side by side.
  - A `max<x` threshold on a `Trend` is evaluated; a crossed one prints `max=` and exits 99. Checked with a
    throwaway script run with `k6 run`.
  - `gracefulStop` given in seconds (for example `130s`) is normalised to `2m10s` by `inspect`.

## Not verified

- Exact end-of-maintenance date for MassTransit 8. Only "through at least the end of
  2026" is official.
- Hangfire license text and which features are paid.
- Whether Wolverine 6.x requires the separate `WolverineFx.RuntimeCompilation` package
  (seen only in a third-party pull request). Not relevant since Wolverine is not used.
- Bitnami chart and image availability. Bitnami changed its free catalog in 2025; this
  project avoids Bitnami for that reason (ADR 0011), and the current terms were not
  re-checked.
- Trademark or company name collisions for "Ledgerline".
- Grafana LGTM image contents per version (which Tempo, Loki, Prometheus builds).
