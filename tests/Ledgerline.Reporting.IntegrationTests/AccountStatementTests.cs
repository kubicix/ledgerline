using System.Net;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Reporting.Consuming;
using Ledgerline.Reporting.Infrastructure;
using Ledgerline.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

[assembly: AssemblyFixture(typeof(InfrastructureFixture))]

namespace Ledgerline.Reporting.IntegrationTests;

/// <summary>
/// The consumer loop and the <c>account_statements</c> projection (reporting.md sections 3, 4, 6 Q1 and 7), driven
/// through real Kafka records into real MongoDB.
/// </summary>
public sealed class AccountStatementTests(InfrastructureFixture infrastructure)
{
    private const string LineType = "accounts.event.journal-entry-line-posted.v1";
    private static readonly TimeSpan KafkaWait = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Ledger_line_record_becomes_statement_document()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        // An instant with an offset and milliseconds: stored as a UTC BSON date, which keeps millisecond precision.
        var line = Line(postedAt: new DateTimeOffset(2026, 11, 13, 13, 16, 2, 123, TimeSpan.FromHours(3)));
        await host.ProduceAsync(host.Options.LedgerEntriesTopic, line.AccountId.ToString(), Record(line));

        var document = await WaitForStatementAsync(host, $"{line.JournalEntryId}:{line.LineNo}");
        document.ShouldBe(new BsonDocument
        {
            { "_id", $"{line.JournalEntryId}:1" },
            { "accountId", line.AccountId.ToString() },
            { "journalEntryId", line.JournalEntryId.ToString() },
            { "entryType", "Transfer" },
            { "direction", "Debit" },
            { "amountMinor", new BsonInt64(2500) },
            { "currency", "TRY" },
            { "balanceAfterMinor", new BsonInt64(7500) },
            { "paymentId", line.PaymentId.ToString() },
            { "postedAt", new BsonDateTime(new DateTime(2026, 11, 13, 10, 16, 2, 123, DateTimeKind.Utc)) }
        });
        document["amountMinor"].BsonType.ShouldBe(BsonType.Int64);
        document["balanceAfterMinor"].BsonType.ShouldBe(BsonType.Int64);
        document["postedAt"].BsonType.ShouldBe(BsonType.DateTime);
        document["_id"].BsonType.ShouldBe(BsonType.String);
        document["accountId"].BsonType.ShouldBe(BsonType.String);
    }

    [Fact]
    public async Task Applying_same_record_twice_leaves_one_identical_document()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        var line = Line();
        var value = Record(line);
        var first = await host.ProduceAsync(host.Options.LedgerEntriesTopic, line.AccountId.ToString(), value);
        await WaitForStatementAsync(host, $"{line.JournalEntryId}:1");
        var before = await StatementsAsync(host);

        // The same record again (a redelivery): same key, same partition, same envelope.
        var second = await host.ProduceAsync(host.Options.LedgerEntriesTopic, line.AccountId.ToString(), value);
        second.Partition.ShouldBe(first.Partition);
        await WaitForCommitAsync(host, second);

        var after = await StatementsAsync(host);
        after.Count.ShouldBe(1);
        after.ShouldBe(before);
    }

    [Fact]
    public Task Offsets_are_not_committed_when_mongo_write_fails() =>
        WriteFailureIsRetriedAsync(new MongoFaults { Failing = true });

    [Fact]
    public Task Write_concern_failure_is_retried_not_dead_lettered() =>
        WriteFailureIsRetriedAsync(new MongoFaults { Failing = true, Error = MongoFaults.WriteConcernTimeout });

    [Fact]
    public async Task Each_dead_lettered_record_is_committed_before_the_next_record_is_tried()
    {
        var key = Ids.New().ToString();
        var poison = new List<DeliveryResult<string, byte[]>>();
        DeliveryResult<string, byte[]>? goodRecord = null;
        var good = Line();
        // Produced before Reporting starts, so the first batch holds all four records.
        await using var host = await ReportingHost.StartAsync(infrastructure, beforeStart: async starting =>
        {
            for (var i = 0; i < 3; i++) poison.Add(await starting.ProduceAsync(starting.Options.LedgerEntriesTopic, key, PoisonRecord()));
            goodRecord = await starting.ProduceAsync(starting.Options.LedgerEntriesTopic, key, Record(good));
        });
        var dead = new TopicPartition(host.Options.DeadLetterTopic, 0);
        using var reader = DeadReader(host);

        // The first poison record's offset is committed while the next ones are still being tried (each takes four
        // retry waits), not only at the end of the batch.
        await Eventually.ShouldAsync(async () => await host.CommittedOffsetAsync(poison[0].TopicPartition) >= poison[0].Offset.Value + 1,
            KafkaWait, "The first dead-lettered record was not committed.");
        reader.QueryWatermarkOffsets(dead, TimeSpan.FromSeconds(10)).High.Value
            .ShouldBeLessThan(poison.Count, "The commit waited for the whole batch.");

        await WaitForCommitAsync(host, goodRecord!);
        await WaitForStatementAsync(host, $"{good.JournalEntryId}:1");
        // Each poison record exactly once, in order: no replay of the batch dead-lettered them again.
        reader.QueryWatermarkOffsets(dead, TimeSpan.FromSeconds(10)).High.Value.ShouldBe(poison.Count);
        reader.Assign(new TopicPartitionOffset(dead, Offset.Beginning));
        var sources = new List<string?>();
        for (var i = 0; i < poison.Count; i++) sources.Add(Header(reader.Consume(KafkaWait).ShouldNotBeNull(), DeadLetterProducer.SourceOffsetHeader));
        reader.Close();
        sources.ShouldBe(poison.Select(x => x.Offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList<string?>());
    }

    private async Task WriteFailureIsRetriedAsync(MongoFaults faults)
    {
        await using var host = await ReportingHost.StartAsync(infrastructure, services =>
        {
            services.RemoveAll<MongoCollections>();
            services.AddSingleton<MongoCollections>(provider => new FaultyMongoCollections(
                provider.GetRequiredService<IMongoDatabase>(), faults, MongoCollections.AccountStatementsName));
        });
        var line = Line();
        var record = await host.ProduceAsync(host.Options.LedgerEntriesTopic, line.AccountId.ToString(), Record(line));

        // More failed writes than the poison limit: a transient failure is retried, never dead-lettered.
        await Eventually.ShouldAsync(() => Task.FromResult(faults.Failures > KafkaConsumerLoop.MaxAttempts + 1), KafkaWait,
            "The loop did not retry the failing write.");
        (await StatementsAsync(host)).ShouldBeEmpty();
        var committed = await host.CommittedOffsetAsync(record.TopicPartition);
        (committed is null || committed <= record.Offset.Value).ShouldBeTrue($"Offset {committed} was committed past a failed write.");

        faults.Failing = false;
        await WaitForStatementAsync(host, $"{line.JournalEntryId}:1");
        await WaitForCommitAsync(host, record);
        using var reader = DeadReader(host);
        reader.QueryWatermarkOffsets(new TopicPartition(host.Options.DeadLetterTopic, 0), TimeSpan.FromSeconds(10)).High.Value
            .ShouldBe(0, "A failed write was dead-lettered.");
    }

    [Fact]
    public async Task Poison_record_goes_to_dead_topic_after_5_attempts()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        var topic = host.Options.LedgerEntriesTopic;
        var key = Ids.New().ToString();
        // A line record whose payload has none of the line's fields: the projection rejects it.
        var poisonValue = EnvelopeSerializer.Serialize(new MessageEnvelope(Ids.New(), LineType, DateTimeOffset.UtcNow,
            Ids.New(), null, null, JsonDocument.Parse("{}").RootElement));
        var poison = await host.ProduceAsync(topic, key, poisonValue);
        // A type no projection handles is skipped, not dead-lettered.
        await host.ProduceAsync(topic, key, EnvelopeSerializer.Serialize(new MessageEnvelope(Ids.New(),
            "accounts.event.account-status-changed.v1", DateTimeOffset.UtcNow, Ids.New(), null, null, JsonDocument.Parse("{}").RootElement)));
        var good = Line();
        var goodRecord = await host.ProduceAsync(topic, key, Record(good));
        goodRecord.Partition.ShouldBe(poison.Partition);

        await WaitForCommitAsync(host, goodRecord);
        await WaitForStatementAsync(host, $"{good.JournalEntryId}:1");
        (await StatementsAsync(host)).Count.ShouldBe(1);

        var dead = new TopicPartition(host.Options.DeadLetterTopic, 0);
        using var consumer = DeadReader(host);
        consumer.QueryWatermarkOffsets(dead, TimeSpan.FromSeconds(10)).High.Value.ShouldBe(1, "Exactly one record is dead-lettered.");
        consumer.Assign(new TopicPartitionOffset(dead, Offset.Beginning));
        var deadRecord = consumer.Consume(KafkaWait).ShouldNotBeNull();
        consumer.Close();

        Encoding.UTF8.GetString(deadRecord.Message.Key).ShouldBe(key);
        deadRecord.Message.Value.ShouldBe(poisonValue);
        Header(deadRecord, DeadLetterProducer.AttemptsHeader).ShouldBe("5");
        Header(deadRecord, DeadLetterProducer.SourceTopicHeader).ShouldBe(topic);
        Header(deadRecord, DeadLetterProducer.SourcePartitionHeader).ShouldBe(poison.Partition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Header(deadRecord, DeadLetterProducer.SourceOffsetHeader).ShouldBe(poison.Offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Header(deadRecord, DeadLetterProducer.ErrorHeader).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Index_on_account_and_posted_at_exists()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        var expected = new BsonDocument { { "accountId", 1 }, { "postedAt", -1 } };
        await Eventually.ShouldAsync(async () =>
        {
            using var cursor = await host.Database.GetCollection<BsonDocument>(MongoCollections.AccountStatementsName).Indexes
                .ListAsync(TestContext.Current.CancellationToken);
            var indexes = await cursor.ToListAsync(TestContext.Current.CancellationToken);
            return indexes.Any(index => index["key"].AsBsonDocument == expected);
        }, KafkaWait, "The statement index was not created.");
    }

    [Fact]
    public async Task Readiness_checks_mongo_only()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        // C3: no broker check, so a Kafka outage never takes the read API out of rotation.
        host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Select(x => x.Name).ShouldBe(["database"]);
        using var client = host.CreateClient();
        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static IConsumer<byte[], byte[]> DeadReader(ReportingHost host) => new ConsumerBuilder<byte[], byte[]>(new ConsumerConfig
    {
        BootstrapServers = host.BootstrapServers,
        GroupId = "dead-reader-" + Guid.NewGuid().ToString("N"),
        EnableAutoCommit = false
    }).Build();

    /// <summary>A line record whose payload has none of the line's fields: the projection rejects it.</summary>
    private static byte[] PoisonRecord() => EnvelopeSerializer.Serialize(new MessageEnvelope(Ids.New(), LineType,
        DateTimeOffset.UtcNow, Ids.New(), null, null, JsonDocument.Parse("{}").RootElement));

    private static JournalEntryLinePosted Line(DateTimeOffset? postedAt = null) =>
        new(Ids.New(), "Transfer", Ids.New(), "TRY", postedAt ?? new DateTimeOffset(2026, 11, 13, 10, 16, 2, TimeSpan.Zero),
            Ids.New(), "Debit", 2500, 7500, 1);

    private static byte[] Record(JournalEntryLinePosted line) => EnvelopeSerializer.Serialize(new MessageEnvelope(Ids.New(),
        LineType, line.PostedAt, line.PaymentId, null, null, EnvelopeSerializer.ToPayload(line)));

    private static async Task<BsonDocument> WaitForStatementAsync(ReportingHost host, string id)
    {
        BsonDocument? document = null;
        await Eventually.ShouldAsync(async () =>
        {
            document = await host.Database.GetCollection<BsonDocument>(MongoCollections.AccountStatementsName)
                .Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
            return document is not null;
        }, KafkaWait, $"Statement {id} was not written.");
        return document!;
    }

    private static Task<List<BsonDocument>> StatementsAsync(ReportingHost host) =>
        host.Database.GetCollection<BsonDocument>(MongoCollections.AccountStatementsName).Find(new BsonDocument())
            .Sort(new BsonDocument("_id", 1)).ToListAsync(TestContext.Current.CancellationToken);

    private static Task WaitForCommitAsync(ReportingHost host, DeliveryResult<string, byte[]> record) =>
        Eventually.ShouldAsync(async () => await host.CommittedOffsetAsync(record.TopicPartition) >= record.Offset.Value + 1,
            KafkaWait, $"Offset {record.Offset.Value} on {record.TopicPartition} was not committed.");

    private static string? Header(ConsumeResult<byte[], byte[]> record, string name) =>
        record.Message.Headers.TryGetLastBytes(name, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
}
