# ADR 0010: Quartz.NET for the reconciliation job (Phase 2)

Date: 2026-11-13. Status: accepted for Phase 2. Versions: ADR 0000.

## Context

End-of-day reconciliation must run once per day, survive restarts, and not run twice
when the service has two replicas. Candidates: Quartz.NET (4.7.3, Apache-2.0, targets
net10.0) and Hangfire (1.8.25, license not verified, some features are paid).

## Decision

Quartz.NET, hosted in the `Reconciliation` service, with the ADO.NET job store on
`reconciliation_db` in clustered mode.

- Clear license, verified.
- Cron triggers, misfire handling, and clustering are built in and free.
- No dashboard needed; runs are visible through the service's own API and metrics.

A Redis distributed lock additionally guards the job body, keyed by business date.
This is defense in depth and the one place the project uses a distributed lock for
mutual exclusion. The job is also idempotent per business date (a unique
`reconciliation_runs.business_date`), so the lock is an optimization, not the guarantee.

## Consequences

- Quartz tables live in `reconciliation_db` and are created by the service migration.
- Short timers elsewhere (saga timeouts, outbox polling) do not use Quartz. They are
  simple hosted loops over a table, which is easier to test deterministically.

## Alternatives considered

- **Hangfire:** good dashboard, but license terms were not verified and the dashboard
  is not needed.
- **Kubernetes CronJob:** fits Kubernetes well, but would not exist under Compose and
  would move schedule logic out of the code the tests can reach. Mentioned in the
  README as a valid production alternative.
