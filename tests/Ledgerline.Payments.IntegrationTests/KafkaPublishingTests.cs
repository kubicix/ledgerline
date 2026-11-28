using System.Net;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Kafka;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Payments.IntegrationTests;

/// <summary>
/// Payments relays its <c>payments.event.*</c> events to Kafka through the outbox when <c>Messaging:Kafka:Enabled</c>
/// is on; commands to Accounts, Risk and itself never get a Kafka row. The topic name is fixed and the module's broker
/// is shared, so every read filters by this test's payment.
/// </summary>
[Collection("Redis lifecycle")]
public sealed class KafkaPublishingTests(InfrastructureFixture infrastructure)
{
    private const string PaymentsTopic = "ledgerline.payments.events.v1";
    private const int Partitions = 6;

    [Fact]
    public async Task Payment_events_are_produced_keyed_by_payment()
    {
        await CreateTopicAsync();
        await using var scenario = await MerchantScenario.StartAsync(infrastructure, paymentSettings: new Dictionary<string, string?>
        {
            ["Messaging:Kafka:Enabled"] = "true",
            ["Messaging:Kafka:BootstrapServers"] = infrastructure.KafkaBootstrapServers
        });
        var id = await scenario.AuthorizeAsync();
        using var capture = await scenario.ActionAsync(id, "capture");
        capture.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await scenario.Probe.WaitForAsync<PaymentCaptured>(x => x.PaymentId == id);

        var records = await ConsumeAsync(2, envelope => envelope.CorrelationId == id);
        records.ShouldAllBe(x => x.Key == id.ToString());
        // One key, one partition, outbox order.
        records.Select(x => x.Envelope.Type).ShouldBe(["payments.event.payment-authorized.v1", "payments.event.payment-captured.v1"]);
        ((PaymentCaptured)EnvelopeSerializer.FromPayload(records[1].Envelope.Payload, typeof(PaymentCaptured))).PaymentId.ShouldBe(id);

        var rows = await OutboxAsync(scenario.Host, id);
        var kafka = rows.Where(x => x.Transport == "kafka").ToList();
        records.Select(x => x.Envelope.MessageId).ShouldBe(kafka.Select(x => x.MessageId));
        // Every event has its RabbitMQ twin with the same payload; no command ever has a Kafka row.
        foreach (var row in kafka)
            rows.Single(x => x.Transport == "rabbitmq" && x.Type == row.Type).Payload.GetRawText().ShouldBe(row.Payload.GetRawText());
        rows.Where(x => x.Type.Contains(".command.", StringComparison.Ordinal)).ShouldNotBeEmpty();
        rows.Where(x => x.Type.Contains(".command.", StringComparison.Ordinal)).ShouldAllBe(x => x.Transport == "rabbitmq");
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task With_kafka_disabled_no_kafka_rows_are_written()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        host.Services.GetService<KafkaTopicMap>().ShouldBeNull();
        host.Services.GetServices<IOutboxPublisher>().Select(x => x.Transport).ShouldBe(["rabbitmq"]);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        using var client = host.CreateClient(owner);
        using var response = await PaymentTestSetup.TransferAsync(client, from, to);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var id = (await PaymentTestSetup.BodyAsync(response)).GetProperty("id").GetGuid();
        var command = await probe.WaitForAsync<TransferFunds>(x => x.PaymentId == id);
        await probe.PublishAsync(new LedgerOperationSucceeded(command.OperationId, id, LedgerOperationKind.TransferFunds, Guid.NewGuid(), DateTimeOffset.UtcNow), id);
        await probe.WaitForAsync<PaymentCompleted>(x => x.PaymentId == id);

        var rows = await OutboxAsync(host, id);
        rows.Select(x => x.Type).ShouldBe(["accounts.command.transfer-funds.v1", "payments.event.payment-completed.v1"]);
        rows.ShouldAllBe(x => x.Transport == "rabbitmq");
    }

    private static async Task<List<OutboxMessage>> OutboxAsync(ServiceHost<Program> host, Guid paymentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(x => x.CorrelationId == paymentId).OrderBy(x => x.Sequence).ToListAsync(TestContext.Current.CancellationToken);
    }

    // The test broker has no kafka-init: the topic is created here, before any host can produce to it.
    private async Task CreateTopicAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = infrastructure.KafkaBootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = PaymentsTopic, NumPartitions = Partitions, ReplicationFactor = 1 }]);
        }
        catch (CreateTopicsException error) when (error.Results.All(x => x.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists)) { }
    }

    private async Task<List<(string Key, MessageEnvelope Envelope)>> ConsumeAsync(int count, Func<MessageEnvelope, bool> mine)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = infrastructure.KafkaBootstrapServers,
            GroupId = $"payments-test-{Guid.NewGuid():N}",
            EnableAutoCommit = false
        }).Build();
        consumer.Assign(Enumerable.Range(0, Partitions).Select(p => new TopicPartitionOffset(PaymentsTopic, p, Offset.Beginning)));
        var records = new List<(string Key, MessageEnvelope Envelope)>();
        await Eventually.ShouldAsync(() =>
        {
            while (records.Count < count && consumer.Consume(TimeSpan.FromMilliseconds(200)) is { } record)
            {
                var envelope = EnvelopeSerializer.Deserialize(record.Message.Value);
                if (mine(envelope)) records.Add((record.Message.Key, envelope));
            }
            return Task.FromResult(records.Count >= count);
        }, TimeSpan.FromSeconds(30), $"Expected {count} Kafka records on {PaymentsTopic}");
        // Nothing more of ours may follow: one record per event, never a duplicate.
        while (consumer.Consume(TimeSpan.FromMilliseconds(500)) is { } extra)
            mine(EnvelopeSerializer.Deserialize(extra.Message.Value)).ShouldBeFalse("A record was produced twice.");
        consumer.Close();
        return records;
    }
}
