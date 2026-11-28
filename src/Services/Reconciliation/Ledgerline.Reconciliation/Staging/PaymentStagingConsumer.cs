using System.Reflection;
using System.Text.Json;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Payments;
using Ledgerline.Reconciliation.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Reconciliation.Staging;

/// <summary>
/// Stages the six payment events of <c>ledgerline.payments.events.v1</c> into <c>staged_payments</c>, one row per
/// payment with its latest status by the event's own time: an older event that arrives later changes nothing, and a
/// bank capture id once staged is kept. A refund does not change a payment's status and is acknowledged unstaged.
/// </summary>
public sealed class PaymentStagingConsumer : IStagingConsumer
{
    private static readonly string RefundedType = NameOf<PaymentRefunded>();

    private static readonly Dictionary<string, Func<JsonElement, Staged>> Parsers = new(StringComparer.Ordinal)
    {
        [NameOf<PaymentAuthorized>()] = payload => Parse<PaymentAuthorized>(payload, e =>
            new(e.PaymentId, e.Kind, "Authorized", e.AmountMinor, e.Currency, null, e.OccurredAt)),
        [NameOf<PaymentCaptured>()] = payload => Parse<PaymentCaptured>(payload, e =>
            new(e.PaymentId, e.Kind, "Captured", e.AmountMinor, e.Currency, null, e.OccurredAt)),
        [NameOf<PaymentCompleted>()] = payload => Parse<PaymentCompleted>(payload, e =>
            new(e.PaymentId, e.Kind, "Completed", e.AmountMinor, e.Currency, e.BankCaptureId, e.OccurredAt)),
        [NameOf<PaymentVoided>()] = payload => Parse<PaymentVoided>(payload, e =>
            new(e.PaymentId, e.Kind, "Voided", e.AmountMinor, e.Currency, null, e.OccurredAt)),
        // The failed event carries its own status (Rejected, Failed, Compensated, ManualReview).
        [NameOf<PaymentFailed>()] = payload => Parse<PaymentFailed>(payload, e =>
            new(e.PaymentId, e.Kind, e.Status, e.AmountMinor, e.Currency, null, e.OccurredAt))
    };

    public IReadOnlyCollection<string> MessageTypes { get; } = [.. Parsers.Keys, RefundedType];

    public async Task ApplyAsync(MessageEnvelope envelope, ReconciliationDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(db);
        if (envelope.Type == RefundedType) return;
        var (e, occurredAt) = StagingConsumerLoop.ReadPayload(() =>
        {
            var parsed = Parsers[envelope.Type](envelope.Payload);
            // Deserialization leaves missing fields at their defaults, which would otherwise be staged as an empty payment.
            if (parsed.PaymentId == Guid.Empty || string.IsNullOrEmpty(parsed.Kind) || string.IsNullOrEmpty(parsed.Status) ||
                string.IsNullOrEmpty(parsed.Currency) || parsed.OccurredAt == default)
                throw new InvalidDataException("The payment event lacks its payment, kind, status, currency or time.");
            return (parsed, parsed.OccurredAt.ToUniversalTime());
        });
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO staged_payments (payment_id, kind, status, amount_minor, currency, bank_capture_id, occurred_at, last_message_id)
            VALUES ({e.PaymentId}, {e.Kind}, {e.Status}, {e.AmountMinor}, {e.Currency}, {e.BankCaptureId}, {occurredAt}, {envelope.MessageId})
            ON CONFLICT (payment_id) DO UPDATE SET
                kind = EXCLUDED.kind,
                status = EXCLUDED.status,
                amount_minor = EXCLUDED.amount_minor,
                currency = EXCLUDED.currency,
                bank_capture_id = COALESCE(EXCLUDED.bank_capture_id, staged_payments.bank_capture_id),
                occurred_at = EXCLUDED.occurred_at,
                last_message_id = EXCLUDED.last_message_id
            WHERE staged_payments.occurred_at <= EXCLUDED.occurred_at
            """, ct);
    }

    private static string NameOf<T>() => typeof(T).GetCustomAttribute<MessageTypeAttribute>()!.Name;

    private static Staged Parse<T>(JsonElement payload, Func<T, Staged> map) => map((T)EnvelopeSerializer.FromPayload(payload, typeof(T)));

    private sealed record Staged(Guid PaymentId, string Kind, string Status, long AmountMinor, string Currency,
        string? BankCaptureId, DateTimeOffset OccurredAt);
}
