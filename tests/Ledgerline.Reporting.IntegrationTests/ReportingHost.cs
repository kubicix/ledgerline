using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Ledgerline.Reporting.Consuming;
using Ledgerline.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Ledgerline.Reporting.IntegrationTests;

/// <summary>
/// Runs Reporting in-process against the fixture's Kafka and MongoDB. Every host gets its own topics, consumer group
/// and database, so tests share the broker and the server without seeing each other's records or documents. The
/// test broker has no kafka-init: the topics are created here before the consumer subscribes. A test that runs
/// Accounts as well passes Accounts' fixed topic names instead and filters by its own IDs.
/// </summary>
public sealed class ReportingHost : IAsyncDisposable
{
    public const int EventPartitions = 6;
    private readonly Action<IServiceCollection>? overrides;
    private readonly IProducer<string, byte[]> producer;
    private readonly IAdminClient admin;
    private WebApplicationFactory<Program>? factory;

    private ReportingHost(IReadOnlyDictionary<string, string?> settings, Action<IServiceCollection>? overrides,
        string bootstrapServers, ConsumerOptions options, IMongoDatabase database)
    {
        Settings = settings;
        this.overrides = overrides;
        factory = new ConfiguredFactory(settings, overrides);
        Options = options;
        Database = database;
        BootstrapServers = bootstrapServers;
        producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = bootstrapServers, Acks = Acks.All }).Build();
        admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
    }

    public IServiceProvider Services => Factory.Services;
    public ConsumerOptions Options { get; }
    public IMongoDatabase Database { get; }
    public string BootstrapServers { get; }

    /// <summary>Reporting's configuration, so a one-shot command runs against the same topics, group and database.</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; }

    /// <param name="beforeStart">
    /// Runs after the topics exist and before Reporting starts, for example to produce records the first batch must
    /// contain. It must not touch <see cref="Services"/>, which would start the host.
    /// </param>
    /// <param name="ledgerEntriesTopic">A fixed ledger topic (Accounts' own name) instead of a fresh one.</param>
    /// <param name="accountEventsTopic">A fixed account events topic (Accounts' own name) instead of a fresh one.</param>
    /// <param name="retryInitialDelay">The first retry wait; 20 ms when null.</param>
    /// <param name="retryMaxDelay">The longest retry wait; 200 ms when null.</param>
    public static async Task<ReportingHost> StartAsync(InfrastructureFixture infrastructure, Action<IServiceCollection>? overrides = null,
        Func<ReportingHost, Task>? beforeStart = null, string? ledgerEntriesTopic = null, string? accountEventsTopic = null,
        TimeSpan? retryInitialDelay = null, TimeSpan? retryMaxDelay = null)
    {
        var initialDelay = retryInitialDelay ?? TimeSpan.FromMilliseconds(20);
        var maxDelay = retryMaxDelay ?? TimeSpan.FromMilliseconds(200);
        ArgumentNullException.ThrowIfNull(infrastructure);
        var suffix = Guid.NewGuid().ToString("N");
        var bootstrapServers = infrastructure.KafkaBootstrapServers;
        var mongo = infrastructure.MongoConnectionString;
        var options = new ConsumerOptions
        {
            BootstrapServers = bootstrapServers,
            GroupId = "reporting-test-" + suffix,
            LedgerEntriesTopic = ledgerEntriesTopic ?? $"test.{suffix}.ledger.entries",
            PaymentEventsTopic = $"test.{suffix}.payments.events",
            AccountEventsTopic = accountEventsTopic ?? $"test.{suffix}.accounts.events",
            DeadLetterTopic = $"test.{suffix}.reporting.dead",
            RetryInitialDelay = initialDelay,
            RetryMaxDelay = maxDelay
        };
        var databaseName = "reporting_" + suffix;
        await CreateTopicsAsync(bootstrapServers, options);
        var settings = new Dictionary<string, string?>
        {
            [ConsumerOptions.BootstrapServersKey] = bootstrapServers,
            [ConsumerOptions.GroupIdKey] = options.GroupId,
            [ConsumerOptions.LedgerEntriesTopicKey] = options.LedgerEntriesTopic,
            [ConsumerOptions.PaymentEventsTopicKey] = options.PaymentEventsTopic,
            [ConsumerOptions.AccountEventsTopicKey] = options.AccountEventsTopic,
            [ConsumerOptions.DeadLetterTopicKey] = options.DeadLetterTopic,
            [ConsumerOptions.RetryInitialDelayMsKey] = ((long)initialDelay.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
            [ConsumerOptions.RetryMaxDelayMsKey] = ((long)maxDelay.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ConnectionStrings:Mongo"] = mongo,
            ["Reporting:Mongo:Database"] = databaseName
        };
        var host = new ReportingHost(settings, overrides, bootstrapServers, options, new MongoClient(mongo).GetDatabase(databaseName));
        try
        {
            if (beforeStart is not null) await beforeStart(host);
            _ = host.Services;
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public HttpClient CreateClient(Guid? userId = null, string roles = "customer")
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (userId is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(userId.Value, roles, null));
        return client;
    }

    /// <param name="timestamp">The record's create time; the producer's clock when null.</param>
    public async Task<DeliveryResult<string, byte[]>> ProduceAsync(string topic, string key, byte[] value, DateTimeOffset? timestamp = null)
    {
        var message = new Message<string, byte[]> { Key = key, Value = value };
        if (timestamp is not null) message.Timestamp = new Timestamp(timestamp.Value);
        var delivery = await producer.ProduceAsync(topic, message, TestContext.Current.CancellationToken);
        delivery.Status.ShouldBe(PersistenceStatus.Persisted);
        return delivery;
    }

    /// <summary>
    /// The group's committed offset for one partition, or null when the group never committed there. A broker that does
    /// not answer within the request timeout also gives null ("not committed yet"), so a caller polling with
    /// <c>Eventually</c> asks again and only its overall timeout can fail the test.
    /// </summary>
    public async Task<long?> CommittedOffsetAsync(TopicPartition partition)
    {
        try
        {
            var results = await admin.ListConsumerGroupOffsetsAsync(
                [new ConsumerGroupTopicPartitions(Options.GroupId, [partition])],
                new ListConsumerGroupOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
            var offset = results.Single().Partitions.Single().Offset;
            return offset == Offset.Unset ? null : offset.Value;
        }
        catch (KafkaException e) when (e.Error.Code is ErrorCode.Local_TimedOut or ErrorCode.RequestTimedOut)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits until the group has committed every record of every partition of the three event topics, up to their end
    /// offsets as they stand when this is called. It reads only Kafka, never the read model.
    /// </summary>
    public async Task WaitForCatchUpAsync(TimeSpan timeout)
    {
        var ends = new List<(TopicPartition Partition, long End)>();
        using (var reader = new ConsumerBuilder<byte[], byte[]>(new ConsumerConfig
               {
                   BootstrapServers = BootstrapServers,
                   GroupId = "watermarks-" + Guid.NewGuid().ToString("N"),
                   EnableAutoCommit = false
               }).Build())
        {
            foreach (var topic in Options.EventTopics)
            {
                var partitions = admin.GetMetadata(topic, TimeSpan.FromSeconds(10)).Topics.Single().Partitions;
                foreach (var metadata in partitions)
                {
                    var partition = new TopicPartition(topic, metadata.PartitionId);
                    ends.Add((partition, reader.QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(10)).High.Value));
                }
            }
        }
        await Eventually.ShouldAsync(async () =>
        {
            foreach (var (partition, end) in ends)
                if ((await CommittedOffsetAsync(partition) ?? 0) < end) return false;
            return true;
        }, timeout, "The Reporting consumer did not commit up to the end of the event topics.");
    }

    /// <summary>Stops Reporting, whose consumer leaves the group. The topics, the group's offsets and the database stay.</summary>
    public async Task StopAsync()
    {
        if (factory is null) return;
        await factory.DisposeAsync();
        factory = null;
    }

    /// <summary>Starts Reporting again on the same topics, group and database.</summary>
    public void StartAgain()
    {
        if (factory is not null) throw new InvalidOperationException("Reporting is already running.");
        factory = new ConfiguredFactory(Settings, overrides);
        _ = factory.Services;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        producer.Dispose();
        admin.Dispose();
        await Database.Client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName);
    }

    private WebApplicationFactory<Program> Factory => factory ?? throw new InvalidOperationException("Reporting is stopped.");

    private static async Task CreateTopicsAsync(string bootstrapServers, ConsumerOptions options)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
        var topics = options.EventTopics.Select(name => new TopicSpecification { Name = name, NumPartitions = EventPartitions, ReplicationFactor = 1 })
            .Append(new TopicSpecification { Name = options.DeadLetterTopic, NumPartitions = 1, ReplicationFactor = 1 });
        try
        {
            await admin.CreateTopicsAsync(topics);
        }
        // Accounts' fixed topics exist already when another test of the module created them.
        catch (CreateTopicsException error) when (error.Results.All(x => x.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists)) { }
    }

    private sealed class ConfiguredFactory(IReadOnlyDictionary<string, string?> settings, Action<IServiceCollection>? overrides)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(Path.GetDirectoryName(typeof(Program).Assembly.Location)!);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>(settings)
            {
                ["Auth:Mode"] = "TestKey",
                ["Auth:Authority"] = TestTokens.Issuer,
                ["Auth:Audience"] = "ledgerline-api",
                ["Auth:TestSigningKey"] = TestTokens.SigningKey
            }));
            if (overrides is not null) builder.ConfigureTestServices(overrides);
        }
    }
}
