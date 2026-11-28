using System.Globalization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Ledgerline.Reconciliation.Staging;
using Ledgerline.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Reconciliation.IntegrationTests;

/// <summary>
/// Runs Reconciliation in-process on its own migrated database and the fixture's Kafka. Every host gets its own topics
/// and consumer group, so test classes share the broker without seeing each other's records. The test broker has no
/// kafka-init: the topics are created here before the consumer subscribes.
/// </summary>
public sealed class StagingHost : IAsyncDisposable
{
    public const int Partitions = 3;
    private static readonly TimeSpan KafkaWait = TimeSpan.FromSeconds(60);
    private readonly ServiceHost<Program> host;
    private readonly IProducer<string, byte[]> producer;
    private readonly IAdminClient admin;

    private StagingHost(ServiceHost<Program> host, StagingOptions options)
    {
        this.host = host;
        Options = options;
        producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = options.BootstrapServers, Acks = Acks.All }).Build();
        admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();
    }

    public StagingOptions Options { get; }
    public string ConnectionString => host.ConnectionString;
    public StagingProgress Progress => host.Services.GetRequiredService<StagingProgress>();

    /// <summary>Every partition of both subscribed topics.</summary>
    public IReadOnlyList<TopicPartition> AllPartitions => Options.Topics
        .SelectMany(topic => Enumerable.Range(0, Partitions).Select(partition => new TopicPartition(topic, partition))).ToList();

    public IServiceProvider Services => host.Services;

    /// <param name="infrastructure">The fixture's containers.</param>
    /// <param name="migrated">False starts the service on an empty database, as before <c>migrate</c> ran.</param>
    /// <param name="extraSettings">Host settings on top of the staging ones. The Quartz scheduler is off unless they turn it on.</param>
    /// <param name="overrides">Service registrations that replace the service's own.</param>
    public static async Task<StagingHost> StartAsync(InfrastructureFixture infrastructure, bool migrated = true,
        IReadOnlyDictionary<string, string?>? extraSettings = null, Action<IServiceCollection>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        var suffix = Guid.NewGuid().ToString("N");
        var options = new StagingOptions
        {
            BootstrapServers = infrastructure.KafkaBootstrapServers,
            GroupId = "reconciliation-test-" + suffix,
            PaymentEventsTopic = $"test.{suffix}.payments.events",
            LedgerEntriesTopic = $"test.{suffix}.ledger.entries"
        };
        await CreateTopicsAsync(options);
        var settings = new Dictionary<string, string?>
        {
            [StagingOptions.BootstrapServersKey] = options.BootstrapServers,
            [StagingOptions.GroupIdKey] = options.GroupId,
            [StagingOptions.PaymentEventsTopicKey] = options.PaymentEventsTopic,
            [StagingOptions.LedgerEntriesTopicKey] = options.LedgerEntriesTopic,
            [StagingOptions.RetryInitialDelayMsKey] = 20.ToString(CultureInfo.InvariantCulture),
            [StagingOptions.RetryMaxDelayMsKey] = 200.ToString(CultureInfo.InvariantCulture),
            // Staging hosts need no scheduler, and on an empty database its schema check would fail the start.
            ["Reconciliation:Scheduler:Enabled"] = "false"
        };
        foreach (var (key, value) in extraSettings ?? new Dictionary<string, string?>()) settings[key] = value;
        var host = migrated
            ? await ServiceHost<Program>.StartAsync(infrastructure, "recon_staging", overrides, settings)
            : await ServiceHost<Program>.StartOnDatabaseAsync(infrastructure, await infrastructure.CreateDatabaseAsync("recon_empty"), overrides, settings);
        return new StagingHost(host, options);
    }

    /// <param name="timestamp">The record's Kafka timestamp; the produce time when null.</param>
    public async Task<DeliveryResult<string, byte[]>> ProduceAsync(string topic, Guid key, byte[] value, DateTimeOffset? timestamp = null)
    {
        var message = new Message<string, byte[]> { Key = key.ToString(), Value = value };
        if (timestamp is { } at) message.Timestamp = new Timestamp(at);
        var delivery = await producer.ProduceAsync(topic, message,
            TestContext.Current.CancellationToken);
        delivery.Status.ShouldBe(PersistenceStatus.Persisted);
        return delivery;
    }

    /// <summary>
    /// Waits until the staging progress reports every given record as committed, then until Kafka holds the group's
    /// offset past it. Only after that can a test assert that a late or duplicate record changed nothing.
    /// </summary>
    public async Task WaitForCommitAsync(IEnumerable<DeliveryResult<string, byte[]>> records)
    {
        foreach (var last in records.GroupBy(x => x.TopicPartition).Select(x => x.MaxBy(record => record.Offset.Value)!))
        {
            await Eventually.ShouldAsync(() => Task.FromResult(Progress.LastCommitted(last.TopicPartition)?.Offset >= last.Offset.Value),
                KafkaWait, $"Staging did not report offset {last.Offset.Value} on {last.TopicPartition} as committed.");
            await Eventually.ShouldAsync(async () => await CommittedOffsetAsync(last.TopicPartition) >= last.Offset.Value + 1,
                KafkaWait, $"Offset {last.Offset.Value} on {last.TopicPartition} was not committed in Kafka.");
        }
    }

    /// <summary>
    /// The group's committed offset, or null when none is committed yet or the broker did not answer in time. Only for
    /// positive checks polled with <c>Eventually</c>; a negative check uses <see cref="CommittedOffsetOrThrowAsync"/>.
    /// </summary>
    public async Task<long?> CommittedOffsetAsync(TopicPartition partition)
    {
        try
        {
            return await CommittedOffsetOrThrowAsync(partition);
        }
        catch (KafkaException e) when (e.Error.Code is ErrorCode.Local_TimedOut or ErrorCode.RequestTimedOut)
        {
            return null;
        }
    }

    /// <summary>The group's committed offset, or null only when none is committed; a broker timeout throws.</summary>
    public async Task<long?> CommittedOffsetOrThrowAsync(TopicPartition partition)
    {
        var results = await admin.ListConsumerGroupOffsetsAsync(
            [new ConsumerGroupTopicPartitions(Options.GroupId, [partition])],
            new ListConsumerGroupOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
        var offset = results.Single().Partitions.Single().Offset;
        return offset == Offset.Unset ? null : offset.Value;
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        producer.Dispose();
        admin.Dispose();
    }

    private static async Task CreateTopicsAsync(StagingOptions options)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();
        try
        {
            // Unlimited retention, as the Compose kafka-init creates the real topics. The run tests produce records with
            // timestamps weeks in the past; under the broker's default 7-day retention its first check (about 30 s after
            // start) deleted them before staging read them, and the consumer silently reset past them.
            await admin.CreateTopicsAsync(options.Topics.Select(name => new TopicSpecification
            {
                Name = name, NumPartitions = Partitions, ReplicationFactor = 1,
                Configs = new Dictionary<string, string> { ["retention.ms"] = "-1", ["cleanup.policy"] = "delete" }
            }));
        }
        catch (CreateTopicsException error) when (error.Results.All(x => x.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists)) { }
    }
}
