using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Reporting.Consuming;
using Ledgerline.Reporting.Infrastructure;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Ledgerline.Reporting.Projections;

/// <summary>
/// One <c>payment_timelines</c> document per payment, <c>_id = paymentId</c> (reporting.md section 4). Two guarded
/// writes, both idempotent:
/// <list type="number">
/// <item>Append the event to <c>events</c> with a filter on <c>events.messageId</c> not holding it, so one event is
/// recorded once (the upsert creates the document on the first event).</item>
/// <item><c>$set</c> the summary only when the stored <c>updatedAt</c> is missing or not newer than the event's
/// <c>occurredAt</c>, so an older event never overwrites the summary of a newer one.</item>
/// </list>
/// The summary is written on every apply, so a crash between the two writes is healed by the redelivery.
/// <c>updatedAt</c> is the <c>occurredAt</c> of the newest event applied to the summary, not the processing time, which
/// keeps the document the same after a rebuild from offset zero.
/// </summary>
public sealed class PaymentTimelineProjection(MongoCollections collections) : IProjection
{
    public IReadOnlyCollection<string> MessageTypes => PaymentEvent.MessageTypes;

    public async Task ApplyAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var paymentEvent = PaymentEvent.From(envelope);
        await AppendEventAsync(paymentEvent, ct);
        await UpdateSummaryAsync(paymentEvent, ct);
    }

    private async Task AppendEventAsync(PaymentEvent paymentEvent, CancellationToken ct)
    {
        var filters = Builders<PaymentTimelineDocument>.Filter;
        var messageId = paymentEvent.MessageId.ToString();
        var filter = filters.And(filters.Eq(x => x.Id, paymentEvent.PaymentId.ToString()), filters.Ne("events.messageId", messageId));
        var updates = Builders<PaymentTimelineDocument>.Update;
        var update = updates.Combine(
            updates.Push(x => x.Events, new PaymentTimelineEvent
            {
                MessageId = messageId,
                Type = paymentEvent.TypeName,
                At = paymentEvent.OccurredAt.UtcDateTime
            }),
            updates.SetOnInsert(x => x.CapturedMinor, 0L),
            updates.SetOnInsert(x => x.RefundedMinor, 0L));
        try
        {
            await collections.PaymentTimelines.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException error) when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // The filter missed because the document already holds the event, and the upsert then tried to insert the
            // same _id: the event is applied. The same write without the upsert settles it either way: it matches
            // nothing for a recorded event and appends the event if the duplicate key came from a concurrent first insert.
            await collections.PaymentTimelines.UpdateOneAsync(filter, update, cancellationToken: ct);
        }
    }

    private async Task UpdateSummaryAsync(PaymentEvent paymentEvent, CancellationToken ct)
    {
        var filters = Builders<PaymentTimelineDocument>.Filter;
        var updatedAt = paymentEvent.OccurredAt.UtcDateTime;
        // Matches a missing updatedAt (the document the append just created) and one that is not newer than the event.
        var filter = filters.And(filters.Eq(x => x.Id, paymentEvent.PaymentId.ToString()), filters.Not(filters.Gt(x => x.UpdatedAt, updatedAt)));
        var updates = Builders<PaymentTimelineDocument>.Update;
        var sets = new List<UpdateDefinition<PaymentTimelineDocument>>
        {
            updates.Set(x => x.Kind, paymentEvent.Kind),
            updates.Set(x => x.AmountMinor, paymentEvent.AmountMinor),
            updates.Set(x => x.Currency, paymentEvent.Currency),
            updates.Set(x => x.CustomerId, paymentEvent.CustomerId.ToString()),
            updates.Set(x => x.SourceAccountId, paymentEvent.SourceAccountId?.ToString()),
            updates.Set(x => x.DestinationAccountId, paymentEvent.DestinationAccountId.ToString()),
            updates.Set(x => x.UpdatedAt, updatedAt)
        };
        // A field the event does not carry keeps its previous value.
        if (paymentEvent.Status is not null) sets.Add(updates.Set(x => x.Status, paymentEvent.Status));
        if (paymentEvent.CapturedMinor is { } captured) sets.Add(updates.Set(x => x.CapturedMinor, captured));
        if (paymentEvent.RefundedTotalMinor is { } refunded) sets.Add(updates.Set(x => x.RefundedMinor, refunded));
        await collections.PaymentTimelines.UpdateOneAsync(filter, updates.Combine(sets), cancellationToken: ct);
    }
}

/// <summary>
/// A payment timeline as stored: IDs are lowercase hyphenated GUID strings, amounts 64-bit integers and instants UTC
/// BSON dates (millisecond precision). The summary fields are absent until the first event is applied to the summary.
/// </summary>
public sealed class PaymentTimelineDocument
{
    [BsonId] public string Id { get; init; } = "";
    [BsonElement("kind")] public string? Kind { get; init; }
    [BsonElement("status")] public string? Status { get; init; }
    [BsonElement("amountMinor")] public long AmountMinor { get; init; }
    [BsonElement("currency")] public string? Currency { get; init; }
    [BsonElement("customerId")] public string? CustomerId { get; init; }
    [BsonElement("sourceAccountId")] public string? SourceAccountId { get; init; }
    [BsonElement("destinationAccountId")] public string? DestinationAccountId { get; init; }
    [BsonElement("capturedMinor")] public long CapturedMinor { get; init; }
    [BsonElement("refundedMinor")] public long RefundedMinor { get; init; }
    [BsonElement("events")] public List<PaymentTimelineEvent> Events { get; init; } = [];
    [BsonElement("updatedAt"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)] public DateTime? UpdatedAt { get; init; }
}

/// <summary>One event of a timeline: its message ID, the short type name and its <c>occurredAt</c>.</summary>
public sealed class PaymentTimelineEvent
{
    [BsonElement("messageId")] public string MessageId { get; init; } = "";
    [BsonElement("type")] public string Type { get; init; } = "";
    [BsonElement("at"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)] public DateTime At { get; init; }
}
