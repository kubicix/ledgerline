using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Reconciliation.Infrastructure;

namespace Ledgerline.Reconciliation.Staging;

/// <summary>
/// Stages the records of one topic. The loop calls <see cref="ApplyAsync"/> for every record whose envelope type is in
/// <see cref="MessageTypes"/>, inside a database transaction it commits before the record's offset. Delivery is at
/// least once, so every write must be idempotent. Invalid data must throw <see cref="InvalidDataException"/> (or fail
/// in the database with a data or constraint error), see <see cref="StagingConsumerLoop.IsPoison"/>; every other
/// exception is retried without limit and the record's offset is not committed.
/// </summary>
public interface IStagingConsumer
{
    IReadOnlyCollection<string> MessageTypes { get; }
    Task ApplyAsync(MessageEnvelope envelope, ReconciliationDbContext db, CancellationToken ct);
}
