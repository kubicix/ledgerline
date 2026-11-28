using Confluent.Kafka;

namespace Ledgerline.Reconciliation.Staging;

/// <summary>The last record of a partition whose staging transaction and offset commit both completed.</summary>
/// <param name="Offset">The record's own offset (the committed offset is one past it).</param>
/// <param name="Timestamp">The record's Kafka timestamp.</param>
public sealed record PartitionProgress(long Offset, DateTimeOffset Timestamp);

/// <summary>
/// What the staging consumer has processed, per topic-partition, and which partitions it currently owns. The run of
/// Task 9 waits on it until both topics are past the end of the business date. A partition's progress stays after it
/// is revoked; only the owned set changes on a rebalance.
/// </summary>
public sealed class StagingProgress
{
    private readonly Lock gate = new();
    private readonly Dictionary<TopicPartition, PartitionProgress> committed = [];
    private readonly HashSet<TopicPartition> owned = [];

    public IReadOnlyCollection<TopicPartition> OwnedPartitions
    {
        get { lock (gate) return owned.ToArray(); }
    }

    public PartitionProgress? LastCommitted(TopicPartition partition)
    {
        lock (gate) return committed.GetValueOrDefault(partition);
    }

    internal void Assigned(IEnumerable<TopicPartition> partitions)
    {
        lock (gate) owned.UnionWith(partitions);
    }

    internal void Revoked(IEnumerable<TopicPartition> partitions)
    {
        lock (gate) owned.ExceptWith(partitions);
    }

    /// <summary>The consumer closed: it owns nothing until the next assignment.</summary>
    internal void Released()
    {
        lock (gate) owned.Clear();
    }

    internal void Committed(TopicPartition partition, PartitionProgress progress)
    {
        lock (gate)
            if (!committed.TryGetValue(partition, out var known) || known.Offset < progress.Offset)
                committed[partition] = progress;
    }
}
