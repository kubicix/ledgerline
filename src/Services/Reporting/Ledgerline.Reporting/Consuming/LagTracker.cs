using System.Diagnostics.Metrics;
using System.Globalization;
using Confluent.Kafka;
using Ledgerline.BuildingBlocks;

namespace Ledgerline.Reporting.Consuming;

/// <summary>
/// How far the read model is behind the event topics (reporting.md section 7), as the gauge
/// <c>ledgerline.reporting.lag</c> (seconds) and the API header <see cref="HeaderName"/> (whole seconds).
/// <para>
/// Definition. A partition is caught up when the next offset to apply has reached the partition's high watermark,
/// as the consumer last saw it. The lag is 0 when every assigned partition is caught up. Otherwise it is the largest,
/// over the partitions that are behind, of now minus the timestamp of the oldest record of that partition that is not
/// applied yet: the age of the oldest change the read model is still missing. That record is known when the loop
/// holds it (taken from Kafka and not yet applied, for example while MongoDB is down). When the loop holds no record of
/// a partition that is behind (fetched by the client, not yet taken), the timestamp of the last record applied on it
/// is used instead, which can only overstate the lag; with nothing applied there either, the partition counts 0.
/// </para>
/// <para>
/// The consumer loop feeds this from its own thread; requests only read the stored state and the clock, so a Kafka
/// outage never blocks or fails an API response. Positions are offsets the loop applied, not fetch positions. A
/// replaced consumer starts from nothing known, so its lag reads 0 until its first batch.
/// </para>
/// </summary>
public sealed class LagTracker
{
    public const string HeaderName = "X-Read-Model-Lag-Seconds";
    private readonly IClock clock;
    private readonly Lock gate = new();
    private readonly Dictionary<TopicPartition, PartitionLag> partitions = [];

    public LagTracker(IClock clock, IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        this.clock = clock;
        meterFactory.Create("Ledgerline").CreateObservableGauge("ledgerline.reporting.lag", () => Lag.TotalSeconds, "s",
            "Age of the oldest event the read model has not applied yet; 0 when the consumer has caught up.");
    }

    /// <summary>The current lag, never negative.</summary>
    public TimeSpan Lag
    {
        get
        {
            var now = clock.UtcNow;
            var lag = TimeSpan.Zero;
            lock (gate)
            {
                foreach (var partition in partitions.Values)
                {
                    var since = partition.Since();
                    if (since is not null && now - since.Value > lag) lag = now - since.Value;
                }
            }
            return lag;
        }
    }

    /// <summary>The header value: the lag in whole seconds, rounded down.</summary>
    public string HeaderValue => ((long)Lag.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>The loop took <paramref name="batch"/> from Kafka and is about to apply it.</summary>
    internal void Taken(IConsumer<byte[], byte[]> consumer, IReadOnlyList<ConsumeResult<byte[], byte[]>> batch) =>
        Update(consumer, batch, applied: 0);

    /// <summary>The first <paramref name="applied"/> records of <paramref name="batch"/> are applied and committed.</summary>
    internal void Applied(IConsumer<byte[], byte[]> consumer, IReadOnlyList<ConsumeResult<byte[], byte[]>> batch, int applied) =>
        Update(consumer, batch, applied);

    /// <summary>The consumer is closed; a new one starts from nothing known.</summary>
    internal void Reset()
    {
        lock (gate) partitions.Clear();
    }

    private void Update(IConsumer<byte[], byte[]> consumer, IReadOnlyList<ConsumeResult<byte[], byte[]>> batch, int applied)
    {
        var assignment = consumer.Assignment;
        var now = clock.UtcNow;
        lock (gate)
        {
            foreach (var revoked in partitions.Keys.Except(assignment).ToList()) partitions.Remove(revoked);
            foreach (var partition in assignment)
            {
                if (!partitions.TryGetValue(partition, out var state)) partitions[partition] = state = new PartitionLag();
                // The client's cached watermark, updated by every fetch response; no broker call.
                var high = consumer.GetWatermarkOffsets(partition).High;
                if (high != Offset.Unset) state.High = high.Value;
            }
            for (var i = 0; i < batch.Count; i++)
            {
                if (!partitions.TryGetValue(batch[i].TopicPartition, out var state)) continue;
                var at = TimestampOf(batch[i], now);
                if (i < applied)
                {
                    state.Next = Math.Max(state.Next ?? 0, batch[i].Offset.Value + 1);
                    state.LastApplied = at;
                }
            }
            // The oldest record not applied yet, per partition: the first one of the batch after the applied prefix.
            foreach (var partition in batch.Select(x => x.TopicPartition).Distinct())
                if (partitions.TryGetValue(partition, out var state))
                    state.OldestPending = batch.Skip(applied).Where(x => x.TopicPartition == partition).Select(x => (DateTimeOffset?)TimestampOf(x, now)).FirstOrDefault();
        }
    }

    /// <summary>A record without a timestamp counts from when the loop saw it.</summary>
    private static DateTimeOffset TimestampOf(ConsumeResult<byte[], byte[]> record, DateTimeOffset now) =>
        record.Message.Timestamp.Type == TimestampType.NotAvailable ? now : new DateTimeOffset(record.Message.Timestamp.UtcDateTime, TimeSpan.Zero);

    private sealed class PartitionLag
    {
        public long? Next { get; set; }
        public long? High { get; set; }
        public DateTimeOffset? OldestPending { get; set; }
        public DateTimeOffset? LastApplied { get; set; }

        /// <summary>The instant the partition's lag counts from, or null when it is caught up or nothing is known.</summary>
        public DateTimeOffset? Since()
        {
            if (OldestPending is not null) return OldestPending;
            return Next is not null && High is not null && Next < High ? LastApplied : null;
        }
    }
}
