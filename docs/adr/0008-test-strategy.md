# ADR 0008: Test strategy

Date: 2026-11-13. Status: accepted. Details and exact scenarios: `docs/testing.md`.

## Decision

1. **Test first.** Every plan task starts with a failing test.
2. **Real infrastructure in integration tests.** PostgreSQL, RabbitMQ, and Redis run
   in Testcontainers. No in-memory database provider and no fake broker: the
   properties under test (transactions, locks, redelivery) exist only in the real
   thing.
3. **Three levels.**
   - Unit: domain rules and the saga transition table. No I/O.
   - Integration: one service in-process (`WebApplicationFactory`) against real
     containers. Other services are replaced by publishing and asserting messages.
   - System: the Compose stack as built images, driven over HTTP through the gateway.
4. **Failure scenarios are tests, not demos.** Broker down, crash mid-saga, duplicate
   delivery, and concurrent transfers each have integration tests with deterministic
   fault injection, and the first three also have a black-box system test that uses
   `docker stop` and `docker kill`.
5. **Deterministic fault injection over sleeps.** Tests replace a DI-registered seam
   (for example the outbox publisher or the acknowledgment step) with a decorator that
   fails or stops the host at an exact point. Tests wait on conditions with a timeout,
   never on fixed delays.
6. **Invariant assertions after every money test.** A shared helper checks: sum of
   balances is zero per currency, every journal entry balances, no wallet is negative,
   projections equal a replay of the event stream.
7. **Architecture tests** enforce service isolation and the rule that consumers go
   through the inbox pipeline.
8. **Tooling.** xunit.v3 on Microsoft.Testing.Platform, Shouldly, Testcontainers,
   `Microsoft.AspNetCore.Mvc.Testing`. FluentAssertions is avoided because its recent
   major versions are commercially licensed (not re-verified today).

## CI mapping

- Pull request: build, unit, integration, architecture tests. Target under 10 minutes.
- Main and nightly: additionally the system tests against the Compose stack, and a
  short k6 smoke run.

## Consequences

- Integration tests need Docker. Local runs and CI both have it.
- Tests are slower than mock-based tests. Containers are shared per test assembly and
  each test class gets a fresh database from a template, to keep runs fast.
- System tests are the flakiest level by nature. They are few (one per failure
  scenario) and never gate pull requests.
