using System.Reflection;
using Confluent.Kafka;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Payments;
using Ledgerline.Reporting.Infrastructure;
using Ledgerline.Reporting.Projections;
using Ledgerline.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Ledgerline.Reporting.IntegrationTests;

/// <summary>
/// The <c>payment_timelines</c> and <c>merchant_daily_totals</c> projections (reporting.md sections 4 and 6 Q1), driven
/// through real Kafka records into real MongoDB. The duplicate tests also assert that nothing reached the dead topic,
/// because a duplicate that was dead-lettered would leave the document unchanged too.
/// </summary>
public sealed class ProjectionTests(InfrastructureFixture infrastructure)
{
    private static readonly TimeSpan KafkaWait = TimeSpan.FromSeconds(60);
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Timeline_collects_events_once_each_and_tracks_latest_status()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        var merchant = Flow.MerchantPayment();
        var authorized = Wrap(new PaymentAuthorized(merchant.PaymentId, merchant.Kind, merchant.CustomerId, 10000, "TRY",
            merchant.Source, merchant.Destination, T0, T0.AddDays(7)));
        var captured = Wrap(new PaymentCaptured(merchant.PaymentId, merchant.Kind, merchant.CustomerId, 10000, "TRY",
            merchant.Source, merchant.Destination, T0.AddMinutes(1), 8000));
        var completed = Wrap(new PaymentCompleted(merchant.PaymentId, merchant.Kind, merchant.CustomerId, 10000, "TRY",
            merchant.Source, merchant.Destination, T0.AddMinutes(2), null));
        // A refund leaves the status as it was (payments.md section 2) and raises the refunded total.
        var refunded = Wrap(new PaymentRefunded(merchant.PaymentId, merchant.Kind, merchant.CustomerId, 10000, "TRY",
            merchant.Source, merchant.Destination, T0.AddMinutes(3), Ids.New(), 2500, 2500));
        var topUp = Flow.TopUp();
        var topUpAuthorized = Wrap(new PaymentAuthorized(topUp.PaymentId, topUp.Kind, topUp.CustomerId, 5000, "TRY",
            topUp.Source, topUp.Destination, T0, T0.AddDays(7)));
        var topUpFailed = Wrap(new PaymentFailed(topUp.PaymentId, topUp.Kind, topUp.CustomerId, 5000, "TRY",
            topUp.Source, topUp.Destination, T0.AddMinutes(5), "ManualReview", "bank-timeout"));

        var delivered = new List<DeliveryResult<string, byte[]>>();
        foreach (var message in new[] { authorized, captured, completed, refunded })
            delivered.Add(await ProduceAsync(host, merchant.PaymentId, message));
        foreach (var message in new[] { topUpAuthorized, topUpFailed })
            delivered.Add(await ProduceAsync(host, topUp.PaymentId, message));
        foreach (var record in delivered) await WaitForCommitAsync(host, record);

        var timeline = await TimelineAsync(host, merchant.PaymentId);
        AssertSame(timeline, new BsonDocument
        {
            { "_id", merchant.PaymentId.ToString() },
            { "kind", "MerchantPayment" },
            { "status", "Completed" },
            { "amountMinor", new BsonInt64(10000) },
            { "currency", "TRY" },
            { "customerId", merchant.CustomerId.ToString() },
            { "sourceAccountId", merchant.Source!.Value.ToString() },
            { "destinationAccountId", merchant.Destination.ToString() },
            { "capturedMinor", new BsonInt64(8000) },
            { "refundedMinor", new BsonInt64(2500) },
            { "events", new BsonArray
                {
                    Entry(authorized, "payment-authorized", T0),
                    Entry(captured, "payment-captured", T0.AddMinutes(1)),
                    Entry(completed, "payment-completed", T0.AddMinutes(2)),
                    Entry(refunded, "payment-refunded", T0.AddMinutes(3))
                }
            },
            { "updatedAt", Date(T0.AddMinutes(3)) }
        });
        timeline["events"].AsBsonArray.Select(x => x["at"].BsonType).ShouldAllBe(x => x == BsonType.DateTime);
        timeline["updatedAt"].BsonType.ShouldBe(BsonType.DateTime);

        // The status of a failed event is the event's own, and a payment without a source keeps null.
        AssertSame(await TimelineAsync(host, topUp.PaymentId), new BsonDocument
        {
            { "_id", topUp.PaymentId.ToString() },
            { "kind", "TopUp" },
            { "status", "ManualReview" },
            { "amountMinor", new BsonInt64(5000) },
            { "currency", "TRY" },
            { "customerId", topUp.CustomerId.ToString() },
            { "sourceAccountId", BsonNull.Value },
            { "destinationAccountId", topUp.Destination.ToString() },
            { "capturedMinor", new BsonInt64(0) },
            { "refundedMinor", new BsonInt64(0) },
            { "events", new BsonArray { Entry(topUpAuthorized, "payment-authorized", T0), Entry(topUpFailed, "payment-failed", T0.AddMinutes(5)) } },
            { "updatedAt", Date(T0.AddMinutes(5)) }
        });
        AssertNothingDeadLettered(host);
    }

    [Fact]
    public async Task Out_of_order_older_event_does_not_overwrite_summary()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        var flow = Flow.MerchantPayment();
        var captured = Wrap(new PaymentCaptured(flow.PaymentId, flow.Kind, flow.CustomerId, 10000, "TRY",
            flow.Source, flow.Destination, T0.AddMinutes(1), 8000));
        var authorized = Wrap(new PaymentAuthorized(flow.PaymentId, flow.Kind, flow.CustomerId, 10000, "TRY",
            flow.Source, flow.Destination, T0, T0.AddDays(7)));

        await ProduceAsync(host, flow.PaymentId, captured);
        var older = await ProduceAsync(host, flow.PaymentId, authorized);
        await WaitForCommitAsync(host, older);

        var timeline = await TimelineAsync(host, flow.PaymentId);
        timeline["status"].AsString.ShouldBe("Captured");
        timeline["capturedMinor"].ShouldBe(new BsonInt64(8000));
        timeline["updatedAt"].ShouldBe(Date(T0.AddMinutes(1)));
        // The older event is still recorded, in arrival order.
        timeline["events"].AsBsonArray.ShouldBe(new BsonArray
        {
            Entry(captured, "payment-captured", T0.AddMinutes(1)),
            Entry(authorized, "payment-authorized", T0)
        });
        AssertNothingDeadLettered(host);
    }

    [Fact]
    public async Task Timeline_duplicate_event_is_not_added_twice()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        var flow = Flow.MerchantPayment();
        var authorized = Wrap(new PaymentAuthorized(flow.PaymentId, flow.Kind, flow.CustomerId, 10000, "TRY",
            flow.Source, flow.Destination, T0, T0.AddDays(7)));
        var first = await ProduceAsync(host, flow.PaymentId, authorized);
        await WaitForCommitAsync(host, first);
        var before = await TimelineAsync(host, flow.PaymentId);

        // The same record again (a redelivery): same key, same partition, same envelope.
        var second = await ProduceAsync(host, flow.PaymentId, authorized);
        second.Partition.ShouldBe(first.Partition);
        await WaitForCommitAsync(host, second);

        var after = await TimelineAsync(host, flow.PaymentId);
        after.ShouldBe(before);
        after["events"].AsBsonArray.Count.ShouldBe(1);
        (await Timelines(host).CountDocumentsAsync(new BsonDocument(), cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);
        AssertNothingDeadLettered(host);
    }

    [Fact]
    public async Task Daily_totals_increment_on_capture_and_refund()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        var merchantAccount = Ids.New();
        var first = Flow.MerchantPayment(merchantAccount);
        var second = Flow.MerchantPayment(merchantAccount);
        var nextDay = Flow.MerchantPayment(merchantAccount);
        var dollars = Flow.MerchantPayment(merchantAccount);
        var transfer = Flow.Transfer();

        var firstAuthorized = Wrap(new PaymentAuthorized(first.PaymentId, first.Kind, first.CustomerId, 10000, "TRY",
            first.Source, first.Destination, T0, T0.AddDays(7)));
        var firstCaptured = Wrap(new PaymentCaptured(first.PaymentId, first.Kind, first.CustomerId, 10000, "TRY",
            first.Source, first.Destination, T0.AddMinutes(1), 8000));
        // 01:00 on the 14th at +03:00 is 22:00 UTC on the 13th: the day is the UTC date of the event.
        var secondCaptured = Wrap(new PaymentCaptured(second.PaymentId, second.Kind, second.CustomerId, 2000, "TRY",
            second.Source, second.Destination, new DateTimeOffset(2026, 11, 14, 1, 0, 0, TimeSpan.FromHours(3)), 2000));
        var firstRefunded = Wrap(new PaymentRefunded(first.PaymentId, first.Kind, first.CustomerId, 10000, "TRY",
            first.Source, first.Destination, T0.AddHours(2), Ids.New(), 2500, 2500));
        var nextDayCaptured = Wrap(new PaymentCaptured(nextDay.PaymentId, nextDay.Kind, nextDay.CustomerId, 700, "TRY",
            nextDay.Source, nextDay.Destination, new DateTimeOffset(2026, 11, 14, 0, 30, 0, TimeSpan.Zero), 700));
        var dollarsCaptured = Wrap(new PaymentCaptured(dollars.PaymentId, dollars.Kind, dollars.CustomerId, 100, "USD",
            dollars.Source, dollars.Destination, T0.AddMinutes(5), 100));
        // Only merchant payments count: a captured transfer is ignored, like every other event type.
        var transferCaptured = Wrap(new PaymentCaptured(transfer.PaymentId, transfer.Kind, transfer.CustomerId, 900, "TRY",
            transfer.Source, transfer.Destination, T0.AddMinutes(6), 900));

        var delivered = new List<DeliveryResult<string, byte[]>>
        {
            await ProduceAsync(host, first.PaymentId, firstAuthorized),
            await ProduceAsync(host, first.PaymentId, firstCaptured),
            await ProduceAsync(host, second.PaymentId, secondCaptured),
            await ProduceAsync(host, first.PaymentId, firstRefunded),
            await ProduceAsync(host, nextDay.PaymentId, nextDayCaptured),
            await ProduceAsync(host, dollars.PaymentId, dollarsCaptured),
            await ProduceAsync(host, transfer.PaymentId, transferCaptured)
        };
        foreach (var record in delivered) await WaitForCommitAsync(host, record);

        var totals = await DailyTotals(host).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1))
            .ToListAsync(TestContext.Current.CancellationToken);
        totals.Select(x => x["_id"].AsString).ShouldBe(
            new[] { $"{merchantAccount}:2026-11-13:TRY", $"{merchantAccount}:2026-11-13:USD", $"{merchantAccount}:2026-11-14:TRY" }.Order(StringComparer.Ordinal));

        // The ids are kept sorted, so the list does not depend on the order partitions were consumed in.
        AssertSame(totals.Single(x => x["_id"] == $"{merchantAccount}:2026-11-13:TRY"),
            Totals($"{merchantAccount}:2026-11-13:TRY", merchantAccount, "2026-11-13", "TRY", captured: 10000, refunded: 2500,
                captureCount: 2, refundCount: 1, appliedIds: Sorted(firstCaptured.Id, secondCaptured.Id, firstRefunded.Id)));
        AssertSame(totals.Single(x => x["_id"] == $"{merchantAccount}:2026-11-14:TRY"),
            Totals($"{merchantAccount}:2026-11-14:TRY", merchantAccount, "2026-11-14", "TRY", captured: 700, refunded: 0,
                captureCount: 1, refundCount: 0, appliedIds: [nextDayCaptured.Id]));
        AssertSame(totals.Single(x => x["_id"] == $"{merchantAccount}:2026-11-13:USD"),
            Totals($"{merchantAccount}:2026-11-13:USD", merchantAccount, "2026-11-13", "USD", captured: 100, refunded: 0,
                captureCount: 1, refundCount: 0, appliedIds: [dollarsCaptured.Id]));
        AssertNothingDeadLettered(host);
    }

    [Fact]
    public async Task Daily_totals_duplicate_message_does_not_double_count()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        var flow = Flow.MerchantPayment();
        var captured = Wrap(new PaymentCaptured(flow.PaymentId, flow.Kind, flow.CustomerId, 10000, "TRY",
            flow.Source, flow.Destination, T0, 8000));
        var refunded = Wrap(new PaymentRefunded(flow.PaymentId, flow.Kind, flow.CustomerId, 10000, "TRY",
            flow.Source, flow.Destination, T0.AddMinutes(1), Ids.New(), 2500, 2500));
        await ProduceAsync(host, flow.PaymentId, captured);
        var last = await ProduceAsync(host, flow.PaymentId, refunded);
        await WaitForCommitAsync(host, last);
        var before = await DailyTotals(host).Find(new BsonDocument()).ToListAsync(TestContext.Current.CancellationToken);
        before.Count.ShouldBe(1);

        // Both messages again (a redelivery), same partition.
        await ProduceAsync(host, flow.PaymentId, captured);
        var again = await ProduceAsync(host, flow.PaymentId, refunded);
        again.Partition.ShouldBe(last.Partition);
        await WaitForCommitAsync(host, again);

        var after = await DailyTotals(host).Find(new BsonDocument()).ToListAsync(TestContext.Current.CancellationToken);
        after.ShouldBe(before);
        after.Single()["captureCount"].ShouldBe(new BsonInt64(1));
        after.Single()["refundCount"].ShouldBe(new BsonInt64(1));
        after.Single()["capturedMinor"].ShouldBe(new BsonInt64(8000));
        after.Single()["refundedMinor"].ShouldBe(new BsonInt64(2500));
        AssertNothingDeadLettered(host);
    }

    /// <summary>
    /// Applied through the projection directly rather than through Kafka: 1 001 sequential records would only make the
    /// test slower, and the cap is a property of the write, not of the consumer.
    /// </summary>
    [Fact]
    public async Task Applied_message_ids_are_capped_at_1000()
    {
        var client = new MongoClient(infrastructure.MongoConnectionString);
        var databaseName = "reporting_cap_" + Guid.NewGuid().ToString("N");
        var database = client.GetDatabase(databaseName);
        try
        {
            var projection = new MerchantDailyTotalsProjection(new MongoCollections(database));
            var flow = Flow.MerchantPayment();
            var messages = Enumerable.Range(0, 1001).Select(i => Wrap(new PaymentCaptured(flow.PaymentId, flow.Kind, flow.CustomerId, 10, "TRY",
                flow.Source, flow.Destination, T0.AddSeconds(i), 1))).ToList();
            foreach (var message in messages) await projection.ApplyAsync(message.Envelope, TestContext.Current.CancellationToken);

            var documents = await database.GetCollection<BsonDocument>(MongoCollections.MerchantDailyTotalsName).Find(new BsonDocument())
                .ToListAsync(TestContext.Current.CancellationToken);
            var document = documents.ShouldHaveSingleItem();
            // The smallest (oldest) id is dropped, the 1 000 largest stay sorted, and no increment is lost.
            var sorted = messages.Select(x => x.Id.ToString()).Order(StringComparer.Ordinal).ToList();
            var kept = document["appliedMessageIds"].AsBsonArray.Select(x => x.AsString).ToList();
            kept.Count.ShouldBe(1000);
            kept.ShouldBe(sorted.Skip(1));
            document["captureCount"].ShouldBe(new BsonInt64(1001));
            document["capturedMinor"].ShouldBe(new BsonInt64(1001));

            // An id still inside the window is a duplicate and changes nothing.
            await projection.ApplyAsync(messages[^1].Envelope, TestContext.Current.CancellationToken);
            var again = await database.GetCollection<BsonDocument>(MongoCollections.MerchantDailyTotalsName).Find(new BsonDocument())
                .SingleAsync(TestContext.Current.CancellationToken);
            again.ShouldBe(document);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, TestContext.Current.CancellationToken);
        }
    }

    private sealed record Flow(Guid PaymentId, string Kind, Guid CustomerId, Guid? Source, Guid Destination)
    {
        public static Flow MerchantPayment(Guid? merchantAccount = null) => new(Ids.New(), "MerchantPayment", Ids.New(), Ids.New(), merchantAccount ?? Ids.New());
        public static Flow Transfer() => new(Ids.New(), "Transfer", Ids.New(), Ids.New(), Ids.New());
        public static Flow TopUp() => new(Ids.New(), "TopUp", Ids.New(), null, Ids.New());
    }

    private sealed record Message(Guid Id, byte[] Value, MessageEnvelope Envelope);

    private static Message Wrap<T>(T payload) where T : notnull
    {
        var type = typeof(T).GetCustomAttribute<MessageTypeAttribute>()!.Name;
        var envelope = new MessageEnvelope(Ids.New(), type, DateTimeOffset.UtcNow, Ids.New(), null, null, EnvelopeSerializer.ToPayload(payload));
        return new Message(envelope.MessageId, EnvelopeSerializer.Serialize(envelope), envelope);
    }

    private static Task<DeliveryResult<string, byte[]>> ProduceAsync(ReportingHost host, Guid paymentId, Message message) =>
        host.ProduceAsync(host.Options.PaymentEventsTopic, paymentId.ToString(), message.Value);

    private static BsonDocument Entry(Message message, string type, DateTimeOffset at) =>
        new() { { "messageId", message.Id.ToString() }, { "type", type }, { "at", Date(at) } };

    private static BsonDateTime Date(DateTimeOffset at) => new(at.UtcDateTime);

    private static BsonDocument Totals(string id, Guid merchant, string day, string currency, long captured, long refunded,
        long captureCount, long refundCount, Guid[] appliedIds) => new()
    {
        { "_id", id },
        { "merchantAccountId", merchant.ToString() },
        { "day", day },
        { "currency", currency },
        { "capturedMinor", new BsonInt64(captured) },
        { "refundedMinor", new BsonInt64(refunded) },
        { "captureCount", new BsonInt64(captureCount) },
        { "refundCount", new BsonInt64(refundCount) },
        { "appliedMessageIds", new BsonArray(appliedIds.Select(x => x.ToString())) }
    };

    private static Guid[] Sorted(params Guid[] ids) => [.. ids.OrderBy(x => x.ToString(), StringComparer.Ordinal)];

    /// <summary>Same fields with equal values and types; the field order is free.</summary>
    private static void AssertSame(BsonDocument actual, BsonDocument expected)
    {
        actual.Names.Order(StringComparer.Ordinal).ShouldBe(expected.Names.Order(StringComparer.Ordinal));
        foreach (var element in expected.Elements) actual[element.Name].ShouldBe(element.Value, element.Name);
    }

    private static IMongoCollection<BsonDocument> Timelines(ReportingHost host) =>
        host.Database.GetCollection<BsonDocument>(MongoCollections.PaymentTimelinesName);

    private static IMongoCollection<BsonDocument> DailyTotals(ReportingHost host) =>
        host.Database.GetCollection<BsonDocument>(MongoCollections.MerchantDailyTotalsName);

    private static Task<BsonDocument> TimelineAsync(ReportingHost host, Guid paymentId) =>
        Timelines(host).Find(new BsonDocument("_id", paymentId.ToString())).SingleAsync(TestContext.Current.CancellationToken);

    private static Task WaitForCommitAsync(ReportingHost host, DeliveryResult<string, byte[]> record) =>
        Eventually.ShouldAsync(async () => await host.CommittedOffsetAsync(record.TopicPartition) >= record.Offset.Value + 1,
            KafkaWait, $"Offset {record.Offset.Value} on {record.TopicPartition} was not committed.");

    private static void AssertNothingDeadLettered(ReportingHost host)
    {
        using var reader = new ConsumerBuilder<byte[], byte[]>(new ConsumerConfig
        {
            BootstrapServers = host.BootstrapServers,
            GroupId = "dead-reader-" + Guid.NewGuid().ToString("N"),
            EnableAutoCommit = false
        }).Build();
        reader.QueryWatermarkOffsets(new TopicPartition(host.Options.DeadLetterTopic, 0), TimeSpan.FromSeconds(10)).High.Value
            .ShouldBe(0, "A record was dead-lettered.");
    }
}
