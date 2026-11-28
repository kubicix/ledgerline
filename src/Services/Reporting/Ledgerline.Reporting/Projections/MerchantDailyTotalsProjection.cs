using System.Globalization;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Reporting.Consuming;
using Ledgerline.Reporting.Infrastructure;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Ledgerline.Reporting.Projections;

/// <summary>
/// Per merchant, UTC day and currency, the captured and refunded totals of merchant payments (reporting.md section 4).
/// A counter cannot be upserted by replacement, so every update is guarded: it matches only while the message ID is not
/// in <c>appliedMessageIds</c>, and it pushes the ID in the same write, keeping the last
/// <see cref="MaxAppliedMessageIds"/> in ascending order. Message IDs are UUIDv7 strings, so ascending order is message
/// time order: the array keeps the newest IDs by message time whatever order partitions were consumed in, which keeps
/// the stored document the same after a rebuild from offset zero. The guard is a window, not a ledger: a redelivery older than the window would be
/// counted again, which is the trade-off the spec states (Kafka redelivery windows are far shorter).
/// </summary>
public sealed class MerchantDailyTotalsProjection(MongoCollections collections) : IProjection
{
    public const int MaxAppliedMessageIds = 1000;
    private const string MerchantPayment = "MerchantPayment";

    public IReadOnlyCollection<string> MessageTypes => PaymentEvent.MessageTypes;

    public async Task ApplyAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var paymentEvent = PaymentEvent.From(envelope);
        if (paymentEvent.Kind != MerchantPayment) return;
        var updates = Builders<MerchantDailyTotalsDocument>.Update;
        UpdateDefinition<MerchantDailyTotalsDocument> counters;
        switch (paymentEvent.TypeName)
        {
            case PaymentEvent.CapturedTypeName:
                counters = updates.Combine(
                    updates.Inc(x => x.CapturedMinor, paymentEvent.CapturedMinor!.Value), updates.Inc(x => x.CaptureCount, 1L),
                    updates.SetOnInsert(x => x.RefundedMinor, 0L), updates.SetOnInsert(x => x.RefundCount, 0L));
                break;
            case PaymentEvent.RefundedTypeName:
                counters = updates.Combine(
                    updates.Inc(x => x.RefundedMinor, paymentEvent.RefundAmountMinor!.Value), updates.Inc(x => x.RefundCount, 1L),
                    updates.SetOnInsert(x => x.CapturedMinor, 0L), updates.SetOnInsert(x => x.CaptureCount, 0L));
                break;
            default:
                return;
        }

        var merchant = paymentEvent.DestinationAccountId.ToString();
        var day = paymentEvent.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var id = $"{merchant}:{day}:{paymentEvent.Currency}";
        var messageId = paymentEvent.MessageId.ToString();
        var filters = Builders<MerchantDailyTotalsDocument>.Filter;
        var filter = filters.And(filters.Eq(x => x.Id, id), filters.AnyNe(x => x.AppliedMessageIds, messageId));
        var update = updates.Combine(counters,
            PushApplied(messageId),
            updates.SetOnInsert(x => x.MerchantAccountId, merchant),
            updates.SetOnInsert(x => x.Day, day),
            updates.SetOnInsert(x => x.Currency, paymentEvent.Currency));
        try
        {
            await collections.MerchantDailyTotals.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException error) when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // The filter missed because the message is already in appliedMessageIds, and the upsert then tried to insert
            // the same _id: the message is applied. The same write without the upsert settles it either way: it matches
            // nothing for an applied message and counts it if the duplicate key came from a concurrent first insert.
            await collections.MerchantDailyTotals.UpdateOneAsync(filter, update, cancellationToken: ct);
        }
    }

    /// <summary>
    /// <c>$push: { $each: [id], $sort: 1, $slice: -1000 }</c>. The driver's <c>PushEach</c> sort takes a document, which
    /// cannot express the scalar <c>$sort: 1</c> that sorts an array of strings, so the operator is written out.
    /// </summary>
    private static UpdateDefinition<MerchantDailyTotalsDocument> PushApplied(string messageId) =>
        new BsonDocumentUpdateDefinition<MerchantDailyTotalsDocument>(new BsonDocument("$push", new BsonDocument("appliedMessageIds",
            new BsonDocument { { "$each", new BsonArray { messageId } }, { "$sort", 1 }, { "$slice", -MaxAppliedMessageIds } })));
}

/// <summary>
/// A daily total as stored: <c>_id = "&lt;merchantAccountId&gt;:&lt;yyyy-MM-dd&gt;:&lt;currency&gt;"</c>, amounts and counters
/// as 64-bit integers, <c>day</c> the UTC date.
/// </summary>
public sealed class MerchantDailyTotalsDocument
{
    [BsonId] public string Id { get; init; } = "";
    [BsonElement("merchantAccountId")] public string MerchantAccountId { get; init; } = "";
    [BsonElement("day")] public string Day { get; init; } = "";
    [BsonElement("currency")] public string Currency { get; init; } = "";
    [BsonElement("capturedMinor")] public long CapturedMinor { get; init; }
    [BsonElement("refundedMinor")] public long RefundedMinor { get; init; }
    [BsonElement("captureCount")] public long CaptureCount { get; init; }
    [BsonElement("refundCount")] public long RefundCount { get; init; }
    [BsonElement("appliedMessageIds")] public List<string> AppliedMessageIds { get; init; } = [];
}
