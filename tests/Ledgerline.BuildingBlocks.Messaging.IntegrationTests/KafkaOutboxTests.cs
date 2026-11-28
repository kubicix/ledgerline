using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Ledgerline.BuildingBlocks.Messaging.IntegrationTests.Support;
using Ledgerline.BuildingBlocks.Messaging.Kafka;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

/// <summary>
/// The outbox relays selected events to Kafka next to RabbitMQ. One test stops the shared Kafka broker, so the class
/// runs in the non-parallel broker collection; that also keeps a RabbitMQ stop of another class out of its timing.
/// </summary>
[Collection("Broker lifecycle")]
public sealed class KafkaOutboxTests(InfrastructureFixture infrastructure)
{
    private const int Partitions = 6;

    [Fact]
    public async Task Event_routed_to_both_transports_writes_two_outbox_rows_with_different_message_ids()
    {
        var cs = await CreateDatabaseAsync();
        using var host = BuildHost(cs, infrastructure.RabbitMqUri, "kafka-unused");
        var correlation = Ids.New();
        var causation = Ids.New();
        await EnqueueAsync(host, correlation, causation, new WidgetMoved("w-1", 1));

        await using var db = OpenDb(cs);
        var rows = await db.Set<OutboxMessage>().OrderBy(x => x.Sequence).ToListAsync(TestContext.Current.CancellationToken);
        rows.Select(x => x.Transport).ShouldBe(["rabbitmq", "kafka"]);
        rows[0].MessageId.ShouldNotBe(rows[1].MessageId);
        rows.ShouldAllBe(x => x.Type == "kafkatest.event.widget-moved.v1" && x.CorrelationId == correlation && x.CausationId == causation);
        rows[1].Payload.GetRawText().ShouldBe(rows[0].Payload.GetRawText());
    }

    [Fact]
    public async Task Expanded_event_writes_one_kafka_row_per_record_with_ids_derived_from_the_source_row()
    {
        var cs = await CreateDatabaseAsync();
        using var host = BuildHost(cs, infrastructure.RabbitMqUri, "kafka-unused");
        await EnqueueAsync(host, Ids.New(), null, new BatchPosted("b-1", ["k-1", "k-2", "k-3"]));

        await using var db = OpenDb(cs);
        var rows = await db.Set<OutboxMessage>().OrderBy(x => x.Sequence).ToListAsync(TestContext.Current.CancellationToken);
        rows.Select(x => x.Transport).ShouldBe(["rabbitmq", "kafka", "kafka", "kafka"]);
        rows[0].Type.ShouldBe("kafkatest.event.batch-posted.v1");
        rows.Skip(1).ShouldAllBe(x => x.Type == "kafkatest.event.batch-line-posted.v1");
        rows.Skip(1).Select(x => ((BatchLinePosted)EnvelopeSerializer.FromPayload(x.Payload, typeof(BatchLinePosted))).LineNo)
            .ShouldBe([1, 2, 3]);
        rows.Select(x => x.MessageId).Distinct().Count().ShouldBe(4);

        var routing = host.Services.GetRequiredService<IOutboxRouting>();
        var message = new BatchPosted("b-1", ["k-1", "k-2", "k-3"]);
        var kafkaIds = Route(routing, message, rows[0].MessageId);
        kafkaIds.ShouldBe(rows.Skip(1).Select(x => x.MessageId));
        Route(routing, message, rows[0].MessageId).ShouldBe(kafkaIds);
        Route(routing, message, Ids.New()).Intersect(kafkaIds).ShouldBeEmpty();
    }

    [Fact]
    public async Task Kafka_row_is_produced_with_configured_key_and_envelope_value()
    {
        var topic = await CreateTopicAsync();
        var cs = await CreateDatabaseAsync();
        var uri = await VirtualHostAsync(cs);
        await using var probe = await BrokerProbe.CreateAsync(uri, "kafkatest.event.#");
        using var host = await StartHostAsync(cs, uri, topic);
        var correlation = Ids.New();
        await EnqueueAsync(host, correlation, null, new WidgetMoved("widget-42", 7));

        var record = (await ConsumeAsync(topic, 1)).Single();
        await probe.WaitForAsync<WidgetMoved>(m => m.Number == 7);
        await Eventually.ShouldAsync(async () => await PendingAsync(cs) == 0);
        await host.StopAsync(TestContext.Current.CancellationToken);
        var kafkaRow = await RowAsync(cs, "kafka");
        record.Message.Key.ShouldBe("widget-42");
        var envelope = EnvelopeSerializer.Deserialize(record.Message.Value);
        envelope.MessageId.ShouldBe(kafkaRow.MessageId);
        envelope.Type.ShouldBe("kafkatest.event.widget-moved.v1");
        envelope.CorrelationId.ShouldBe(correlation);
        EnvelopeSerializer.FromPayload(envelope.Payload, typeof(WidgetMoved)).ShouldBe(new WidgetMoved("widget-42", 7));
        var rabbit = probe.Messages.Single();
        rabbit.MessageId.ShouldBe((await RowAsync(cs, "rabbitmq")).MessageId);
        envelope.Payload.GetRawText().ShouldBe(rabbit.Payload.GetRawText());
    }

    [Fact]
    public async Task Kafka_outage_does_not_delay_rabbit_delivery()
    {
        var topic = await CreateTopicAsync();
        var cs = await CreateDatabaseAsync();
        var uri = await VirtualHostAsync(cs);
        await using var probe = await BrokerProbe.CreateAsync(uri, "kafkatest.event.#");
        // A long poll interval: both deliveries depend on the commit signal reaching each transport loop.
        using var host = await StartHostAsync(cs, uri, topic, pollMs: 10_000);
        await infrastructure.StopKafkaAsync();
        try
        {
            await EnqueueAsync(host, Ids.New(), null, new WidgetMoved("widget-1", 1));
            await probe.WaitForAsync<WidgetMoved>(m => m.Number == 1, TimeSpan.FromSeconds(2));
            await Eventually.ShouldAsync(async () => (await RowAsync(cs, "rabbitmq")).ProcessedAt is not null, TimeSpan.FromSeconds(2));
            // The produce against the stopped broker fails within its bound and the row gets a backoff, still pending.
            await Eventually.ShouldAsync(async () => (await RowAsync(cs, "kafka")).Attempts > 0, TimeSpan.FromSeconds(15));
            var pending = await RowAsync(cs, "kafka");
            pending.ProcessedAt.ShouldBeNull();
            pending.LastError.ShouldNotBeNullOrWhiteSpace();
        }
        finally { await infrastructure.StartKafkaAsync(); }
        var record = (await ConsumeAsync(topic, 1, TimeSpan.FromSeconds(90))).Single();
        await Eventually.ShouldAsync(async () => (await RowAsync(cs, "kafka")).ProcessedAt is not null, TimeSpan.FromSeconds(30));
        await host.StopAsync(TestContext.Current.CancellationToken);
        record.Message.Key.ShouldBe("widget-1");
        EnvelopeSerializer.Deserialize(record.Message.Value).MessageId.ShouldBe((await RowAsync(cs, "kafka")).MessageId);
        probe.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Commit_signal_wakes_every_transport_loop()
    {
        var topic = await CreateTopicAsync();
        var cs = await CreateDatabaseAsync();
        var uri = await VirtualHostAsync(cs);
        await using var probe = await BrokerProbe.CreateAsync(uri, "kafkatest.event.#");
        var scans = new ScanObserver();
        using var host = await StartHostAsync(cs, uri, topic, pollMs: 10_000, observer: scans);
        // Both loops finish their first scan first, so the next delivery can only come from the commit signal.
        await Eventually.ShouldAsync(() => Task.FromResult(scans.Transports.Contains("rabbitmq") && scans.Transports.Contains("kafka")));
        await EnqueueAsync(host, Ids.New(), null, new WidgetMoved("widget-2", 2));

        await probe.WaitForAsync<WidgetMoved>(m => m.Number == 2, TimeSpan.FromSeconds(3));
        (await ConsumeAsync(topic, 1, TimeSpan.FromSeconds(5))).Single().Message.Key.ShouldBe("widget-2");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Host_stops_promptly_while_kafka_is_down()
    {
        var topic = await CreateTopicAsync();
        var cs = await CreateDatabaseAsync();
        var uri = await VirtualHostAsync(cs);
        await using var probe = await BrokerProbe.CreateAsync(uri, "kafkatest.event.#");
        // A 60 s produce bound: a relay that ignored cancellation would hold the stop until the 30 s host shutdown timeout.
        var host = await StartHostAsync(cs, uri, topic, messageTimeoutMs: 60_000);
        await infrastructure.StopKafkaAsync();
        try
        {
            await EnqueueAsync(host, Ids.New(), null, new WidgetMoved("widget-3", 3));
            await probe.WaitForAsync<WidgetMoved>(m => m.Number == 3);
            // The Kafka loop is now inside a produce that cannot succeed; stopping must not wait for it.
            var watch = Stopwatch.StartNew();
            await host.StopAsync(TestContext.Current.CancellationToken);
            // A cancelled produce lets the stop finish well under a second; 5 s leaves room for a slow host.
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
            host.Dispose();
            // Disposal adds at most the publisher's 2 s bounded flush.
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            (await RowAsync(cs, "kafka")).ProcessedAt.ShouldBeNull();
        }
        finally
        {
            host.Dispose();
            await infrastructure.StartKafkaAsync();
        }
    }

    [Fact]
    public void Producer_is_idempotent_with_acks_all()
    {
        var config = KafkaOutboxPublisher.CreateProducerConfig(new KafkaOptions { BootstrapServers = "localhost:9092" });
        config.EnableIdempotence.ShouldBe(true);
        config.Acks.ShouldBe(Acks.All);
        // A produce against a dead broker must fail within the relay's confirm bound, not librdkafka's 300 s default.
        config.MessageTimeoutMs.ShouldNotBeNull();
        config.MessageTimeoutMs.Value.ShouldBeInRange(1, 5000);
        config.BootstrapServers.ShouldBe("localhost:9092");
    }

    [Fact]
    public async Task Records_with_same_key_keep_order()
    {
        var topic = await CreateTopicAsync();
        var cs = await CreateDatabaseAsync();
        var uri = await VirtualHostAsync(cs);
        await using var probe = await BrokerProbe.CreateAsync(uri, "kafkatest.event.#");
        using var host = await StartHostAsync(cs, uri, topic);
        await EnqueueAsync(host, Ids.New(), null, Enumerable.Range(1, 100).Select(n => new WidgetMoved("same-key", n)).ToArray());

        var records = await ConsumeAsync(topic, 100, TimeSpan.FromSeconds(60));
        await host.StopAsync(TestContext.Current.CancellationToken);
        records.ShouldAllBe(r => r.Message.Key == "same-key");
        records.Select(r => r.Partition.Value).Distinct().Count().ShouldBe(1);
        records.Select(r => ((WidgetMoved)EnvelopeSerializer.FromPayload(EnvelopeSerializer.Deserialize(r.Message.Value).Payload,
            typeof(WidgetMoved))).Number).ShouldBe(Enumerable.Range(1, 100));
    }

    private static Guid[] Route(IOutboxRouting routing, BatchPosted message, Guid sourceId) =>
        routing.Route(typeof(BatchPosted), message, sourceId).Where(r => r.Transport == "kafka").Select(r => r.MessageId).ToArray();

    private IHost BuildHost(string cs, string rabbitUri, string topic, int pollMs = 20, IInterceptor? observer = null,
        int messageTimeoutMs = 5000)
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:RabbitMq:Uri"] = rabbitUri,
            ["Messaging:Relay:PollIntervalMs"] = pollMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Messaging:Kafka:BootstrapServers"] = topic == "kafka-unused" ? "localhost:1" : infrastructure.KafkaBootstrapServers,
            ["Messaging:Kafka:MessageTimeoutMs"] = messageTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        builder.Services.AddDbContext<TestDbContext>(options =>
        {
            options.UseNpgsql(cs);
            if (observer is not null) options.AddInterceptors(observer);
        });
        builder.Services.AddLedgerlineMessaging<TestDbContext>(configuration, m => m
            .Service("kafka" + Ids.New().ToString("N"))
            .AlsoPublishToKafka<WidgetMoved>(topic, e => e.WidgetId)
            .AlsoPublishToKafkaExpanded<BatchPosted, BatchLinePosted>(topic,
                e => e.Keys.Select((key, index) => new BatchLinePosted(e.BatchId, index + 1, key)), r => r.Key));
        return builder.Build();
    }

    private async Task<IHost> StartHostAsync(string cs, string rabbitUri, string topic, int pollMs = 20,
        IInterceptor? observer = null, int messageTimeoutMs = 5000)
    {
        var host = BuildHost(cs, rabbitUri, topic, pollMs, observer, messageTimeoutMs);
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task EnqueueAsync<T>(IHost host, Guid correlation, Guid? causation, params T[] messages)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        foreach (var message in messages) outbox.Enqueue(message, correlation, causation);
        await scope.ServiceProvider.GetRequiredService<TestDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> CreateTopicAsync()
    {
        var topic = $"kafkatest-{Guid.NewGuid():N}";
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = infrastructure.KafkaBootstrapServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = Partitions, ReplicationFactor = 1 }]);
        return topic;
    }

    private async Task<List<ConsumeResult<string, byte[]>>> ConsumeAsync(string topic, int count, TimeSpan? timeout = null)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = infrastructure.KafkaBootstrapServers,
            GroupId = $"kafkatest-{Guid.NewGuid():N}",
            EnableAutoCommit = false
        }).Build();
        consumer.Assign(Enumerable.Range(0, Partitions).Select(p => new TopicPartitionOffset(topic, p, Offset.Beginning)));
        var records = new List<ConsumeResult<string, byte[]>>();
        await Eventually.ShouldAsync(() =>
        {
            while (records.Count < count && consumer.Consume(TimeSpan.FromMilliseconds(200)) is { } record) records.Add(record);
            return Task.FromResult(records.Count >= count);
        }, timeout ?? TimeSpan.FromSeconds(30), $"Expected {count} Kafka records on {topic}");
        consumer.Close();
        return records;
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var cs = await infrastructure.CreateDatabaseAsync("kafka");
        await using var db = OpenDb(cs);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return cs;
    }

    private async Task<string> VirtualHostAsync(string cs) =>
        await infrastructure.CreateRabbitMqUriAsync(new NpgsqlConnectionStringBuilder(cs).Database!);

    private static TestDbContext OpenDb(string cs) => new(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(cs).Options);

    private static async Task<OutboxMessage> RowAsync(string cs, string transport)
    {
        await using var db = OpenDb(cs);
        return await db.Set<OutboxMessage>().SingleAsync(x => x.Transport == transport, TestContext.Current.CancellationToken);
    }

    private static async Task<int> PendingAsync(string cs)
    {
        await using var db = OpenDb(cs);
        return await db.Set<OutboxMessage>().CountAsync(x => x.ProcessedAt == null, TestContext.Current.CancellationToken);
    }

    private sealed class ScanObserver : DbCommandInterceptor
    {
        private readonly ConcurrentDictionary<string, bool> transports = new(StringComparer.Ordinal);
        public ICollection<string> Transports => transports.Keys;

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SKIP LOCKED", StringComparison.Ordinal))
                foreach (DbParameter parameter in command.Parameters)
                    if (parameter.Value is string transport) transports.TryAdd(transport, true);
            return ValueTask.FromResult(result);
        }
    }

    [MessageType("kafkatest.event.widget-moved.v1")]
    public sealed record WidgetMoved(string WidgetId, int Number);
    [MessageType("kafkatest.event.batch-posted.v1")]
    public sealed record BatchPosted(string BatchId, string[] Keys);
    [MessageType("kafkatest.event.batch-line-posted.v1")]
    public sealed record BatchLinePosted(string BatchId, int LineNo, string Key);
}
