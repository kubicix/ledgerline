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
/// test broker has no kafka-init: the topics are created here before the consumer subscribes.
/// </summary>
public sealed class ReportingHost : IAsyncDisposable
{
    public const int EventPartitions = 6;
    private readonly WebApplicationFactory<Program> factory;
    private readonly IProducer<string, byte[]> producer;
    private readonly IAdminClient admin;

    private ReportingHost(WebApplicationFactory<Program> factory, string bootstrapServers, ConsumerOptions options, IMongoDatabase database)
    {
        this.factory = factory;
        Options = options;
        Database = database;
        BootstrapServers = bootstrapServers;
        producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = bootstrapServers, Acks = Acks.All }).Build();
        admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
    }

    public IServiceProvider Services => factory.Services;
    public ConsumerOptions Options { get; }
    public IMongoDatabase Database { get; }
    public string BootstrapServers { get; }

    public static async Task<ReportingHost> StartAsync(InfrastructureFixture infrastructure, Action<IServiceCollection>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        var suffix = Guid.NewGuid().ToString("N");
        var bootstrapServers = infrastructure.KafkaBootstrapServers;
        var mongo = infrastructure.MongoConnectionString;
        var options = new ConsumerOptions
        {
            BootstrapServers = bootstrapServers,
            GroupId = "reporting-test-" + suffix,
            LedgerEntriesTopic = $"test.{suffix}.ledger.entries",
            PaymentEventsTopic = $"test.{suffix}.payments.events",
            AccountEventsTopic = $"test.{suffix}.accounts.events",
            DeadLetterTopic = $"test.{suffix}.reporting.dead",
            RetryInitialDelay = TimeSpan.FromMilliseconds(20),
            RetryMaxDelay = TimeSpan.FromMilliseconds(200)
        };
        var databaseName = "reporting_" + suffix;
        await CreateTopicsAsync(bootstrapServers, options);
        var factory = new ConfiguredFactory(new Dictionary<string, string?>
        {
            [ConsumerOptions.BootstrapServersKey] = bootstrapServers,
            [ConsumerOptions.GroupIdKey] = options.GroupId,
            [ConsumerOptions.LedgerEntriesTopicKey] = options.LedgerEntriesTopic,
            [ConsumerOptions.PaymentEventsTopicKey] = options.PaymentEventsTopic,
            [ConsumerOptions.AccountEventsTopicKey] = options.AccountEventsTopic,
            [ConsumerOptions.DeadLetterTopicKey] = options.DeadLetterTopic,
            [ConsumerOptions.RetryInitialDelayMsKey] = "20",
            [ConsumerOptions.RetryMaxDelayMsKey] = "200",
            ["ConnectionStrings:Mongo"] = mongo,
            ["Reporting:Mongo:Database"] = databaseName
        }, overrides);
        try
        {
            _ = factory.Services;
            return new ReportingHost(factory, bootstrapServers, options, new MongoClient(mongo).GetDatabase(databaseName));
        }
        catch
        {
            await factory.DisposeAsync();
            throw;
        }
    }

    public HttpClient CreateClient(Guid? userId = null, string roles = "customer")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (userId is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(userId.Value, roles, null));
        return client;
    }

    public async Task<DeliveryResult<string, byte[]>> ProduceAsync(string topic, string key, byte[] value)
    {
        var delivery = await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = key, Value = value }, TestContext.Current.CancellationToken);
        delivery.Status.ShouldBe(PersistenceStatus.Persisted);
        return delivery;
    }

    /// <summary>The group's committed offset for one partition, or null when the group never committed there.</summary>
    public async Task<long?> CommittedOffsetAsync(TopicPartition partition)
    {
        var results = await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(Options.GroupId, [partition])]);
        var offset = results.Single().Partitions.Single().Offset;
        return offset == Offset.Unset ? null : offset.Value;
    }

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
        producer.Dispose();
        admin.Dispose();
        await Database.Client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName);
    }

    private static async Task CreateTopicsAsync(string bootstrapServers, ConsumerOptions options)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
        var topics = options.EventTopics.Select(name => new TopicSpecification { Name = name, NumPartitions = EventPartitions, ReplicationFactor = 1 })
            .Append(new TopicSpecification { Name = options.DeadLetterTopic, NumPartitions = 1, ReplicationFactor = 1 });
        await admin.CreateTopicsAsync(topics);
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
