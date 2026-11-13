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
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL | EF Core provider |
| StackExchange.Redis | 3.4.35 | MIT | Idempotency cache, velocity windows, locks |
| Yarp.ReverseProxy | 2.3.0 | MIT | Gateway. Targets net8.0 at most; runs on net10.0 |
| OpenTelemetry.* (Extensions.Hosting, OTLP exporter, AspNetCore instrumentation) | 1.21.0 | Apache-2.0 | Observability |
| Microsoft.Extensions.Http.Resilience | 10.11.0 | MIT | Bank HTTP client retries and timeouts |
| xunit.v3 | 4.0.3 | Apache-2.0 | Tests |
| Shouldly | 4.3.0 | BSD-3-Clause | Assertions |
| Testcontainers (PostgreSql, RabbitMq, and others) | 4.16.0 | MIT | Integration and failure tests |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.15 | MIT | In-process service hosts in tests |
| Quartz | 4.7.3 | Apache-2.0 | Phase 2 reconciliation schedule |
| MongoDB.Driver | 3.14.0 | Apache-2.0 | Phase 2 read model |
| Confluent.Kafka | 2.16.0 | Apache-2.0 | Phase 2 event stream |

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
| apache/kafka | 4.4.0 | Phase 2. KRaft mode, no ZooKeeper |
| mongo | 9.0 (9.0.8) | Phase 2. Re-verify at M5; 8.0 is the fallback |
| grafana/k6 | 2.4.0 | Load tests |

## Tools and GitHub Actions

| Item | Version |
|---|---|
| Docker Engine / Compose (local) | 29.9.2 / v5.6.0 |
| kind | v0.33.0 (not installed locally yet) |
| Helm | v4.3.2 (not installed locally yet) |
| actions/checkout | v7 |
| actions/setup-dotnet | v6 |
| actions/upload-artifact | v7 |
| actions/dependency-review-action | v5 |
| gitleaks/gitleaks-action | v3 |
| aquasecurity/trivy-action | v0.36.0 |
| helm/kind-action | v1.15.2 |
| azure/setup-helm | v5 |
| grafana/setup-k6-action | v1 |
| github/codeql-action | Major version **not verified** (the releases API returns bundle tags). Check the action README at M3 |

## Not verified

- Exact end-of-maintenance date for MassTransit 8. Only "through at least the end of
  2026" is official.
- Hangfire license text and which features are paid.
- Whether Wolverine 6.x requires the separate `WolverineFx.RuntimeCompilation` package
  (seen only in a third-party pull request). Not relevant since Wolverine is not used.
- `github/codeql-action` current major version.
- Whether the gitleaks GitHub Action needs a license key for this repository type
  (historically required for organizations, free for personal accounts).
- Bitnami chart and image availability. Bitnami changed its free catalog in 2025; this
  project avoids Bitnami for that reason (ADR 0011), and the current terms were not
  re-checked.
- Trademark or company name collisions for "Ledgerline".
- Grafana LGTM image contents per version (which Tempo, Loki, Prometheus builds).
