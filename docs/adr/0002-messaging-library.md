# ADR 0002: Messaging on the raw RabbitMQ client, no bus library

Date: 2026-11-13. Status: accepted (owner decision). Versions: ADR 0000.

## Context

Three options were checked against official sources on 2026-11-13.

| Option | License | Support status | Fit |
|---|---|---|---|
| MassTransit 9 (9.2.6) | Commercial | Active | Paid license; not acceptable for a public portfolio repository |
| MassTransit 8 (8.5.12) | Apache-2.0 | Patches promised "through at least the end of 2026"; a community fork (OpenTransit) is announced | Well known in job postings, but a new project would start on a line that stops receiving fixes within weeks |
| Wolverine 6 (6.64.0) | MIT | Active, 7.0 in alpha | Durable inbox and outbox with EF Core, sagas, RabbitMQ and Kafka transports. Fastest path |
| RabbitMQ.Client 7 (7.2.2) | Apache-2.0 OR MPL-2.0 | Maintained by the RabbitMQ team | Transport only; everything else is ours |

## Decision

Use `RabbitMQ.Client` 7 directly. Build a small messaging layer in
`Ledgerline.BuildingBlocks.Messaging`: envelope, topology declaration, publisher with
confirms, consumer with retry and dead-letter handling, outbox relay, inbox.
The saga is an explicit state machine in Payments (ADR 0006).

Reasons:

1. **Evidence.** The repository must prove understanding of outbox, inbox, and
   idempotent consumers. With a library the tests would prove a configuration. Here
   they prove code that can be read and explained line by line.
2. **License stability.** No dependency on a messaging framework's licensing or
   support timeline.
3. **Interview value.** "RabbitMQ" is the keyword in postings. MassTransit knowledge
   can be discussed through this ADR: what the library would have given for free.

## Consequences

- More code to write and test: about 10 to 12 plan tasks that a library would remove.
- We own the hard parts: reconnects, publisher confirms, poison messages, ordering,
  trace propagation. Each one gets a test.
- The layer must stay small. Explicitly out of scope: message scheduling in the broker,
  request and response abstraction, automatic topology by convention, multiple
  serializers, batching consumers. If the layer starts growing toward a framework, stop
  and reconsider Wolverine.
- Risk of flaky failure tests. Mitigated by deterministic fault injection (ADR 0008).

## What a library would have given for free

Kept as an honest note for the README: Wolverine or MassTransit provide retries with
policies, delayed redelivery, saga persistence, an outbox integrated with the
DbContext, consumer concurrency controls, and telemetry out of the box. In a product
with a deadline, the recommendation would be Wolverine.
