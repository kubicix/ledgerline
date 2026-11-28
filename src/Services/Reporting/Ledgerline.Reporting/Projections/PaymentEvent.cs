using System.Reflection;
using System.Text.Json;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Payments;

namespace Ledgerline.Reporting.Projections;

/// <summary>
/// The six payment event contracts read as one shape, so the timeline and the daily totals share one parser. Every
/// value comes from the record itself (the envelope's message ID and the payload), never from the processing time.
/// </summary>
internal sealed record PaymentEvent(
    Guid MessageId,
    string TypeName,
    Guid PaymentId,
    string Kind,
    Guid CustomerId,
    long AmountMinor,
    string Currency,
    Guid? SourceAccountId,
    Guid DestinationAccountId,
    DateTimeOffset OccurredAt,
    string? Status,
    long? CapturedMinor,
    long? RefundAmountMinor,
    long? RefundedTotalMinor)
{
    public const string CapturedTypeName = "payment-captured";
    public const string RefundedTypeName = "payment-refunded";

    private static readonly Dictionary<string, Func<JsonElement, PaymentEventBody>> Parsers = new(StringComparer.Ordinal)
    {
        [NameOf<PaymentAuthorized>()] = payload => Parse<PaymentAuthorized>(payload, e => Body(e.PaymentId, e.Kind, e.CustomerId, e.AmountMinor,
            e.Currency, e.SourceAccountId, e.DestinationAccountId, e.OccurredAt, "Authorized")),
        [NameOf<PaymentCaptured>()] = payload => Parse<PaymentCaptured>(payload, e => Body(e.PaymentId, e.Kind, e.CustomerId, e.AmountMinor,
            e.Currency, e.SourceAccountId, e.DestinationAccountId, e.OccurredAt, "Captured") with { CapturedMinor = e.CapturedMinor }),
        [NameOf<PaymentCompleted>()] = payload => Parse<PaymentCompleted>(payload, e => Body(e.PaymentId, e.Kind, e.CustomerId, e.AmountMinor,
            e.Currency, e.SourceAccountId, e.DestinationAccountId, e.OccurredAt, "Completed")),
        [NameOf<PaymentVoided>()] = payload => Parse<PaymentVoided>(payload, e => Body(e.PaymentId, e.Kind, e.CustomerId, e.AmountMinor,
            e.Currency, e.SourceAccountId, e.DestinationAccountId, e.OccurredAt, "Voided")),
        // The failed event carries its own status (Rejected, Failed, Compensated, ManualReview).
        [NameOf<PaymentFailed>()] = payload => Parse<PaymentFailed>(payload, e => Body(e.PaymentId, e.Kind, e.CustomerId, e.AmountMinor,
            e.Currency, e.SourceAccountId, e.DestinationAccountId, e.OccurredAt,
            string.IsNullOrEmpty(e.Status) ? throw new InvalidDataException("The failed event has no status.") : e.Status)),
        // A refund does not change the payment status (payments.md section 2): Status stays null.
        [NameOf<PaymentRefunded>()] = payload => Parse<PaymentRefunded>(payload, e => Body(e.PaymentId, e.Kind, e.CustomerId, e.AmountMinor,
            e.Currency, e.SourceAccountId, e.DestinationAccountId, e.OccurredAt, null)
            with { RefundAmountMinor = e.RefundAmountMinor, RefundedTotalMinor = e.RefundedTotalMinor })
    };

    /// <summary>The full envelope type names of the six payment events.</summary>
    public static IReadOnlyCollection<string> MessageTypes { get; } = Parsers.Keys.ToArray();

    /// <summary>Reads an envelope of one of <see cref="MessageTypes"/>; invalid data throws <see cref="InvalidDataException"/>.</summary>
    public static PaymentEvent From(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!Parsers.TryGetValue(envelope.Type, out var parse))
            throw new InvalidDataException("The envelope is not a payment event.");
        var body = parse(envelope.Payload);
        // Deserialization leaves missing fields at their defaults, which would otherwise be stored as an empty payment.
        if (body.PaymentId == Guid.Empty || body.CustomerId == Guid.Empty || body.DestinationAccountId == Guid.Empty ||
            string.IsNullOrEmpty(body.Kind) || string.IsNullOrEmpty(body.Currency) || body.OccurredAt == default)
            throw new InvalidDataException("The payment event lacks its payment, customer, destination, kind, currency or time.");
        return new PaymentEvent(envelope.MessageId, ShortName(envelope.Type), body.PaymentId, body.Kind, body.CustomerId, body.AmountMinor,
            body.Currency, body.SourceAccountId, body.DestinationAccountId, body.OccurredAt, body.Status, body.CapturedMinor,
            body.RefundAmountMinor, body.RefundedTotalMinor);
    }

    /// <summary><c>payments.event.payment-authorized.v1</c> becomes <c>payment-authorized</c>.</summary>
    private static string ShortName(string type) => type.Split('.')[2];

    private static string NameOf<T>() => typeof(T).GetCustomAttribute<MessageTypeAttribute>()!.Name;

    private static PaymentEventBody Parse<T>(JsonElement payload, Func<T, PaymentEventBody> map) =>
        map((T)EnvelopeSerializer.FromPayload(payload, typeof(T)));

    private static PaymentEventBody Body(Guid paymentId, string kind, Guid customerId, long amountMinor, string currency,
        Guid? source, Guid destination, DateTimeOffset occurredAt, string? status) =>
        new(paymentId, kind, customerId, amountMinor, currency, source, destination, occurredAt, status, null, null, null);

    private sealed record PaymentEventBody(Guid PaymentId, string Kind, Guid CustomerId, long AmountMinor, string Currency,
        Guid? SourceAccountId, Guid DestinationAccountId, DateTimeOffset OccurredAt, string? Status,
        long? CapturedMinor, long? RefundAmountMinor, long? RefundedTotalMinor);
}
