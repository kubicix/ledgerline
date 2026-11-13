# When would I build this as a monolith instead?

Draft. The numbers marked "to be measured" are filled in after M3.

## Short answer

Almost always, at this size. If Ledgerline were a product I had to ship and run, I
would start it as a modular monolith with one PostgreSQL database, and I would keep it
that way until a specific pressure forced a split. This repository is built as
microservices because its purpose is to demonstrate the distributed patterns and
their cost. That is a learning and evidence goal, not an architecture recommendation.

In my day job I run a modular monolith in production and chose it deliberately. This
project is the other side of that decision, built so I can compare from experience.

## What the monolith version would look like

- One deployable. Modules `Accounts`, `Payments`, `Risk`, `Notifications`, each with
  its own schema in one database and its own internal API. No module reads another
  module's tables.
- A transfer is one database transaction: risk check, balance check, journal entry,
  payment record. No saga, no timeouts, no `ManualReview` for internal steps.
- The outbox still exists, but only at the real boundaries: the bank and email.
  Those calls cannot join a database transaction in any architecture.
- The saga shrinks to the part that talks to the bank. Compensation remains for
  "bank captured, ledger refused", because that problem is caused by the external
  system, not by the service split.
- Idempotency keys on the HTTP API stay exactly the same.
- Double-entry bookkeeping and the event-sourced ledger stay exactly the same.

So roughly half of what this repository demonstrates is needed either way
(idempotency, the ledger, the bank saga, reconciliation), and the other half exists
only because of the split (inter-service messaging, inbox per service, replicated
`account_refs`, per-service deployment, distributed tracing across the broker).

## What the split cost here

| Cost | Monolith | This repository |
|---|---|---|
| A transfer | One function call and one transaction | Two broker round trips, three local transactions, eventual consistency |
| Transfer latency, p95 | to be measured (single transaction baseline) | to be measured |
| Things that can be down independently | Database | Database, broker, four services, gateway |
| New failure states | None | `Processing` for a long time, `ManualReview`, stale `account_refs` |
| Code that exists only for distribution | None | Messaging layer, saga timeouts, replicas, contract versioning |
| Local development | `dotnet run` | A dozen containers |
| Debugging a failed payment | One log, one stack trace | A distributed trace across four services |
| Deployment | One image, one migration | Five images, five migration jobs, a chart |

The plan includes a small benchmark that runs the same transfer as one local
transaction inside Accounts, to put a real number next to the distributed one.

## What the split bought here

Honestly, little that this system needs:

- Notifications or Risk can be down without stopping the ledger. In a monolith a
  bulkhead and a queue in front of email give most of that.
- Risk rules could be released independently. With one developer that is not a need.
- Each service can scale independently. The measured bottleneck is the ledger's row
  locks on hot accounts, which no service split improves.

## When I would split

I would move a module out of the monolith when at least one of these is true, and I
could name the evidence:

1. **Team boundaries.** Several teams change the same deployable and block each
   other's releases. This is the strongest reason and the one this project cannot
   demonstrate.
2. **Different scaling or runtime profile, measured.** For example Notifications
   fan-out or a read model dominating CPU while the ledger idles.
3. **Different availability requirement.** The ledger must be up; reporting may be
   down for an hour. Splitting lets me spend reliability effort where it matters.
4. **Compliance or blast-radius isolation.** A component that handles card data
   belongs in the smallest possible deployable for audit scope.
5. **Different technology is clearly better for one module.** For example a stream
   processor for reconciliation at a volume PostgreSQL batch jobs cannot handle.

And I would split in this order, because of how cheaply each one separates:

1. Notifications: pure consumer, no one waits for it.
2. Reporting: read-only, rebuildable.
3. Risk: needs a reply, but tolerates being asynchronous.
4. Payments and the ledger last, if ever. Their consistency needs are the tightest.

## When I would not split even then

- If the reason is "microservices scale better" without a measurement.
- If the modules still share a database. That is a distributed monolith: all the
  network failure modes and none of the independence.
- If the team cannot yet operate what it has: no tracing, no automated deployment, no
  on-call. The split multiplies operational work.

## What I keep from this project either way

- Idempotency at every boundary, with the database as the guarantee and the cache as
  an optimization.
- An append-only, double-entry ledger with invariants that tests check after every
  scenario.
- The outbox for every call that leaves the transaction.
- Failure tests as part of the definition of done.
- Module boundaries strict enough that a later split is a deployment decision.

## Limits of this comparison

This is one developer's project with synthetic load on a laptop. It is not banking
experience and it is not evidence about large organizations. The comparison is about
this system at this size.
