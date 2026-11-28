using System.Diagnostics;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Ledgerline.Reconciliation.Staging;

namespace Ledgerline.Reconciliation.Runs;

/// <summary>
/// Step 3: waits until both Kafka consumers are past the end of the business date E (the next UTC midnight). A topic is
/// caught up when the staging consumer owns at least one of its partitions and every owned partition either has a
/// committed record with a Kafka timestamp at or after E, or has nothing left to read: its committed position (the
/// later of <see cref="StagingProgress"/> and the group's offset in Kafka, which also covers the time before a restart)
/// has reached the partition's high watermark, both asked at check time. A partition that keeps receiving records before
/// E, an unreachable broker or no assignment keep it waiting until <see cref="RunOptions.InputWaitTimeout"/>.
/// </summary>
/// <remarks>
/// Only the partitions this replica's consumer owns are judged (ruling J9). With several replicas sharing the group, a
/// run on one replica does not wait for the partitions another replica owns; this is accepted (orchestrator ruling on
/// the M5-T09 concerns). A stricter rule would compare the group's committed offset with the high watermark of every
/// partition of both topics.
/// </remarks>
public sealed partial class KafkaInputWait(StagingOptions staging, StagingProgress progress, RunOptions options, ILogger<KafkaInputWait> logger)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>True when both topics caught up; false when the wait timed out (the run is then flagged <c>inputs_incomplete</c>).</summary>
    public async Task<bool> WaitAsync(DateOnly businessDate, CancellationToken ct)
    {
        var end = new DateTimeOffset(businessDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var waited = Stopwatch.StartNew();
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = staging.BootstrapServers }).Build();
        while (!await CaughtUpAsync(admin, end))
        {
            if (waited.Elapsed >= options.InputWaitTimeout)
            {
                InputsIncomplete(logger, businessDate, options.InputWaitTimeout);
                return false;
            }
            await Task.Delay(options.InputPollInterval, ct);
        }
        return true;
    }

    private async Task<bool> CaughtUpAsync(IAdminClient admin, DateTimeOffset end)
    {
        var owned = progress.OwnedPartitions;
        if (staging.Topics.Any(topic => owned.All(partition => partition.Topic != topic))) return false;
        var open = owned.Where(partition => !(progress.LastCommitted(partition)?.Timestamp >= end)).ToList();
        if (open.Count == 0) return true;
        try
        {
            var high = await HighWatermarksAsync(admin, open);
            var committed = await GroupOffsetsAsync(admin, open);
            return open.All(partition => Position(partition, committed) >= high[partition]);
        }
        catch (KafkaException error)
        {
            // Not caught up yet: the next poll asks again, and the timeout bounds the wait.
            BrokerUnavailable(logger, error.Error.Code, error.Error.Reason);
            return false;
        }
    }

    private long Position(TopicPartition partition, Dictionary<TopicPartition, long> committed) =>
        Math.Max(progress.LastCommitted(partition) is { } last ? last.Offset + 1 : 0, committed.GetValueOrDefault(partition));

    private static async Task<Dictionary<TopicPartition, long>> HighWatermarksAsync(IAdminClient admin, List<TopicPartition> partitions)
    {
        var result = await admin.ListOffsetsAsync(
            partitions.Select(partition => new TopicPartitionOffsetSpec { TopicPartition = partition, OffsetSpec = OffsetSpec.Latest() }),
            new ListOffsetsOptions { RequestTimeout = RequestTimeout });
        return result.ResultInfos.ToDictionary(x => x.TopicPartitionOffsetError.TopicPartition, x => x.TopicPartitionOffsetError.Offset.Value);
    }

    private async Task<Dictionary<TopicPartition, long>> GroupOffsetsAsync(IAdminClient admin, List<TopicPartition> partitions)
    {
        var results = await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(staging.GroupId, partitions)],
            new ListConsumerGroupOffsetsOptions { RequestTimeout = RequestTimeout });
        return results.Single().Partitions.Where(x => x.Offset != Offset.Unset).ToDictionary(x => x.TopicPartition, x => x.Offset.Value);
    }

    [LoggerMessage(1, LogLevel.Warning, "The Kafka inputs of {BusinessDate} were not caught up after {Timeout}; the run proceeds flagged inputs_incomplete")]
    private static partial void InputsIncomplete(ILogger logger, DateOnly businessDate, TimeSpan timeout);
    [LoggerMessage(2, LogLevel.Debug, "Asking Kafka for the input progress failed with {Code}: {Reason}")]
    private static partial void BrokerUnavailable(ILogger logger, ErrorCode code, string reason);
}
