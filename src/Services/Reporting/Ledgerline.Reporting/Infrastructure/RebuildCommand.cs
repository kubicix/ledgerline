using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Ledgerline.Reporting.Consuming;
using MongoDB.Driver;

namespace Ledgerline.Reporting.Infrastructure;

/// <summary>
/// <c>rebuild</c>: rebuilds the read model from offset zero (reporting.md section 6, Q2). It runs while no Reporting
/// instance runs, and it exits when done; the next start replays every topic from the beginning.
/// <list type="number">
/// <item>Refuse when the consumer group has an active member: a running consumer would keep committing its offsets.</item>
/// <item>Drop every read model collection.</item>
/// <item>Set the group's committed offset of every partition of the three event topics to that partition's earliest
/// offset.</item>
/// <item>Create the indexes again.</item>
/// </list>
/// The collections go first: if a later step fails, the command fails and is run again, and a start in between finds
/// empty collections behind old offsets instead of old documents that a replay would count a second time. Running it
/// again is safe.
/// </summary>
public static class RebuildCommand
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(IConfiguration configuration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = ConsumerOptions.From(configuration);
        using var client = MongoCollections.CreateClient(configuration);
        var database = MongoCollections.OpenDatabase(client, configuration);
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();

        await EnsureNoActiveMembersAsync(admin, options.GroupId);
        foreach (var name in MongoCollections.Names) await database.DropCollectionAsync(name, ct);
        await ResetOffsetsAsync(admin, options);
        await new MongoCollections(database).EnsureIndexesAsync(ct);
    }

    private static async Task EnsureNoActiveMembersAsync(IAdminClient admin, string groupId)
    {
        ConsumerGroupDescription group;
        try
        {
            group = (await admin.DescribeConsumerGroupsAsync([groupId])).ConsumerGroupDescriptions.Single();
        }
        // A group that never existed has no members.
        catch (DescribeConsumerGroupsException error) when (error.Results.ConsumerGroupDescriptions.All(x => x.Error.Code == ErrorCode.GroupIdNotFound))
        {
            return;
        }
        if (group.Members.Count > 0)
            throw new InvalidOperationException(
                $"Consumer group '{groupId}' has {group.Members.Count} active member(s). Stop every Reporting instance before the rebuild.");
    }

    private static async Task ResetOffsetsAsync(IAdminClient admin, ConsumerOptions options)
    {
        var partitions = new List<TopicPartition>();
        foreach (var topic in options.EventTopics)
        {
            var metadata = admin.GetMetadata(topic, MetadataTimeout).Topics.Single();
            if (metadata.Error.IsError) throw new InvalidOperationException($"Topic '{topic}' cannot be described: {metadata.Error.Reason}");
            partitions.AddRange(metadata.Partitions.Select(x => new TopicPartition(topic, x.PartitionId)));
        }
        var earliest = await admin.ListOffsetsAsync(partitions.Select(x => new TopicPartitionOffsetSpec { TopicPartition = x, OffsetSpec = OffsetSpec.Earliest() }));
        var offsets = earliest.ResultInfos.Select(x => new TopicPartitionOffset(x.TopicPartitionOffsetError.TopicPartition, x.TopicPartitionOffsetError.Offset)).ToList();
        // Fails, among others, when a consumer joined the group after the check above.
        await admin.AlterConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitionOffsets(options.GroupId, offsets)]);
    }
}
