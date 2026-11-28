using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Kafka;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Accounts.IntegrationTests;

/// <summary>
/// Accounts relays ledger lines and account openings to Kafka through the outbox when <c>Messaging:Kafka:Enabled</c>
/// is on. The topics have fixed names and the module's broker is shared, so every read filters by this test's IDs.
/// </summary>
[Collection("Dependency lifecycle")]
public sealed class KafkaPublishingTests(InfrastructureFixture infrastructure)
{
    private const string EntriesTopic = "ledgerline.ledger.entries.v1";
    private const string AccountsTopic = "ledgerline.accounts.events.v1";
    private const int Partitions = 6;

    [Fact]
    public async Task Posting_produces_one_kafka_record_per_line_keyed_by_account()
    {
        await CreateTopicsAsync();
        await using var host = await StartAsync(kafka: true);
        var (wallet, _) = await WalletsAsync(host);
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.#");
        var payment = Ids.New();
        await probe.PublishAsync(new CreditTopUp(Ids.New(), payment, wallet, 1000, "TRY"), payment);
        var journal = await probe.WaitForAsync<JournalEntryPosted>(x => x.PaymentId == payment);
        journal.Lines.Count.ShouldBe(2);

        var records = (await ConsumeAsync(EntriesTopic, journal.Lines.Count,
                envelope => Line(envelope).JournalEntryId == journal.JournalEntryId))
            .OrderBy(x => Line(x.Envelope).LineNo).ToList();
        records.Select(x => Line(x.Envelope).LineNo).ShouldBe([1, 2]);
        for (var i = 0; i < records.Count; i++)
        {
            var (key, envelope) = records[i];
            var line = Line(envelope);
            var expected = journal.Lines[i];
            envelope.Type.ShouldBe("accounts.event.journal-entry-line-posted.v1");
            envelope.CorrelationId.ShouldBe(payment);
            key.ShouldBe(expected.AccountId.ToString());
            line.ShouldBe(new JournalEntryLinePosted(journal.JournalEntryId, journal.EntryType, journal.PaymentId,
                journal.Currency, journal.PostedAt, expected.AccountId, expected.Direction, expected.AmountMinor,
                expected.BalanceAfterMinor, i + 1));
        }

        // The record IDs are the Kafka rows' IDs, and the same expansion of the same RabbitMQ row gives them again.
        var rows = await OutboxAsync(host);
        var source = rows.Single(x => x.Transport == "rabbitmq" && x.Type == "accounts.event.journal-entry-posted.v1");
        var kafkaIds = rows.Where(x => x.Transport == "kafka" && x.Type == "accounts.event.journal-entry-line-posted.v1")
            .OrderBy(x => x.Sequence).Select(x => x.MessageId).ToArray();
        records.Select(x => x.Envelope.MessageId).ShouldBe(kafkaIds);
        host.Services.GetRequiredService<IOutboxRouting>().Route(typeof(JournalEntryPosted), journal, source.MessageId)
            .Where(x => x.Transport == "kafka").Select(x => x.MessageId).ShouldBe(kafkaIds);
        // Only the journal event is routed: the ledger reply to Payments stays on RabbitMQ alone.
        rows.Where(x => x.Transport == "kafka").ShouldAllBe(x => x.Type == "accounts.event.journal-entry-line-posted.v1");
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    [Fact]
    public async Task Account_opening_produces_a_kafka_record_keyed_by_account()
    {
        await CreateTopicsAsync();
        await using var host = await StartAsync(kafka: true);
        var owner = Ids.New();
        var id = await OpenAccountAsync(host, owner);

        var (key, envelope) = (await ConsumeAsync(AccountsTopic, 1,
            x => ((AccountOpened)EnvelopeSerializer.FromPayload(x.Payload, typeof(AccountOpened))).AccountId == id)).Single();
        key.ShouldBe(id.ToString());
        envelope.Type.ShouldBe("accounts.event.account-opened.v1");
        ((AccountOpened)EnvelopeSerializer.FromPayload(envelope.Payload, typeof(AccountOpened))).OwnerId.ShouldBe(owner);
        var rows = await OutboxAsync(host);
        envelope.MessageId.ShouldBe(rows.Single(x => x.Transport == "kafka").MessageId);
        // The row's payload comes back from jsonb, so compare the messages, not the raw text.
        EnvelopeSerializer.FromPayload(rows.Single(x => x.Transport == "rabbitmq").Payload, typeof(AccountOpened))
            .ShouldBe(EnvelopeSerializer.FromPayload(envelope.Payload, typeof(AccountOpened)));
    }

    [Fact]
    public async Task With_kafka_disabled_no_kafka_rows_are_written()
    {
        await using var host = await StartAsync(kafka: false);
        host.Services.GetService<KafkaTopicMap>().ShouldBeNull();
        host.Services.GetServices<IOutboxPublisher>().Select(x => x.Transport).ShouldBe(["rabbitmq"]);
        await OpenAccountAsync(host, Ids.New());
        var (wallet, _) = await WalletsAsync(host);
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.#");
        var payment = Ids.New();
        await probe.PublishAsync(new CreditTopUp(Ids.New(), payment, wallet, 1000, "TRY"), payment);
        await probe.WaitForAsync<JournalEntryPosted>(x => x.PaymentId == payment);

        var rows = await OutboxAsync(host);
        rows.Select(x => x.Type).Order(StringComparer.Ordinal).ShouldBe(["accounts.event.account-opened.v1",
            "accounts.event.journal-entry-posted.v1", "accounts.event.ledger-operation-succeeded.v1"]);
        rows.ShouldAllBe(x => x.Transport == "rabbitmq");
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    private Task<ServiceHost<Program>> StartAsync(bool kafka) =>
        ServiceHost<Program>.StartAsync(infrastructure, "kafka_publish", settings: kafka
            ? new Dictionary<string, string?>
            {
                ["Messaging:Kafka:Enabled"] = "true",
                ["Messaging:Kafka:BootstrapServers"] = infrastructure.KafkaBootstrapServers
            }
            : null);

    private static async Task<Guid> OpenAccountAsync(ServiceHost<Program> host, Guid owner)
    {
        using var client = host.CreateClient(owner);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/accounts") { Content = JsonContent.Create(new { type = "CustomerWallet", currency = "TRY" }) };
        request.Headers.Add("Idempotency-Key", Ids.New().ToString("N"));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
    }

    private static async Task<(Guid From, Guid To)> WalletsAsync(ServiceHost<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        return (await TestAccounts.OpenWalletAsync(db, Ids.New(), "TRY"), await TestAccounts.OpenWalletAsync(db, Ids.New(), "TRY"));
    }

    private static async Task<List<OutboxMessage>> OutboxAsync(ServiceHost<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AccountsDbContext>().Set<OutboxMessage>().AsNoTracking()
            .OrderBy(x => x.Sequence).ToListAsync(TestContext.Current.CancellationToken);
    }

    private static JournalEntryLinePosted Line(MessageEnvelope envelope) =>
        (JournalEntryLinePosted)EnvelopeSerializer.FromPayload(envelope.Payload, typeof(JournalEntryLinePosted));

    // The test broker has no kafka-init: the topics are created here, before any host can produce to them.
    private async Task CreateTopicsAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = infrastructure.KafkaBootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(new[] { EntriesTopic, AccountsTopic }.Select(name =>
                new TopicSpecification { Name = name, NumPartitions = Partitions, ReplicationFactor = 1 }));
        }
        catch (CreateTopicsException error) when (error.Results.All(x => x.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists)) { }
    }

    private async Task<List<(string Key, MessageEnvelope Envelope)>> ConsumeAsync(string topic, int count, Func<MessageEnvelope, bool> mine)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = infrastructure.KafkaBootstrapServers,
            GroupId = $"accounts-test-{Guid.NewGuid():N}",
            EnableAutoCommit = false
        }).Build();
        consumer.Assign(Enumerable.Range(0, Partitions).Select(p => new TopicPartitionOffset(topic, p, Offset.Beginning)));
        var records = new List<(string Key, MessageEnvelope Envelope)>();
        await Eventually.ShouldAsync(() =>
        {
            while (records.Count < count && consumer.Consume(TimeSpan.FromMilliseconds(200)) is { } record)
            {
                var envelope = EnvelopeSerializer.Deserialize(record.Message.Value);
                if (mine(envelope)) records.Add((record.Message.Key, envelope));
            }
            return Task.FromResult(records.Count >= count);
        }, TimeSpan.FromSeconds(30), $"Expected {count} Kafka records on {topic}");
        // Nothing more of ours may follow: one record per line, never a duplicate.
        while (consumer.Consume(TimeSpan.FromMilliseconds(500)) is { } extra)
            mine(EnvelopeSerializer.Deserialize(extra.Message.Value)).ShouldBeFalse("A record was produced twice.");
        consumer.Close();
        return records;
    }
}
