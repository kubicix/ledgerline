using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Reporting.Consuming;

/// <summary>
/// One read model fed by the consumer loop. The loop calls <see cref="ApplyAsync"/> for every record whose envelope
/// type is in <see cref="MessageTypes"/>, in partition order, and commits the offset only after every matching
/// projection returned. Processing is at least once, so every write must be idempotent (reporting.md section 4).
/// An exception that <see cref="KafkaConsumerLoop.IsTransient"/> classifies as MongoDB being unavailable is retried
/// without limit; any other exception counts as a failed attempt of the record, and the fifth sends it to the dead
/// topic. A guarded upsert whose filter excludes an already applied message fails with a duplicate key error on
/// redelivery: the projection must treat that error as "already applied", not let it escape.
/// </summary>
public interface IProjection
{
    IReadOnlyCollection<string> MessageTypes { get; }
    Task ApplyAsync(MessageEnvelope envelope, CancellationToken ct);
}
