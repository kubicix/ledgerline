using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.Contracts.Reconciliation;
using Ledgerline.Notifications.Infrastructure;
using Ledgerline.Notifications.Templates;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Notifications.Features.Reconciliation;

/// <summary>
/// Plans one mail to the configured operations address (<c>Notifications:OperationsEmail</c>) for a mismatch a reconciliation
/// run found (notifications.md sections 2 and 3). Like every handler it only writes the row; the sender sends it. The address is
/// read when the message is handled and never logged. A missing address is treated like an unknown recipient: the message is
/// retried and then stored as <c>Skipped</c> with reason <c>RecipientUnknown</c>.
/// </summary>
public sealed class MismatchDetectedHandler(NotificationsDbContext db, IConfiguration configuration, ConsumerOptions options, IClock clock)
    : IMessageHandler<ReconciliationMismatchDetected>
{
    /// <summary>The <c>recipient_owner_id</c> of every operations mail: a fixed id that no user has and that is not <see cref="Guid.Empty"/>, which marks an unknown recipient.</summary>
    public static readonly Guid OperationsOwnerId = Guid.Parse("0f5c1d2e-8a47-4b69-9c30-6d1e7f2a4b85");

    public async Task HandleAsync(ReconciliationMismatchDetected message, MessageContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        var address = configuration["Notifications:OperationsEmail"];
        var unknown = string.IsNullOrWhiteSpace(address);
        if (unknown && context.Attempt < options.MaxAttempts) throw new InvalidOperationException("The operations address is not configured.");
        var status = unknown ? "Skipped" : "Pending";
        var reason = unknown ? "RecipientUnknown" : null;
        var rendered = TemplateRenderer.RenderMismatch(new(message.RunId, message.BusinessDate, message.Code, message.Reference,
            message.BankAmountMinor, message.LedgerAmountMinor, message.Currency));
        // The reference is a payment id, a bank capture id or a currency; only the canonical payment id form links the row to a payment.
        Guid? paymentId = Guid.TryParseExact(message.Reference, "D", out var parsed) ? parsed : null;
        var now = clock.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications (id, source_message_id, recipient_owner_id, recipient_email, template,
                payment_id, subject, body, status, reason, attempts, next_attempt_at, created_at)
            VALUES ({Guid.NewGuid()}, {context.MessageId}, {OperationsOwnerId}, {address?.Trim() ?? ""}, {TemplateRenderer.MismatchTemplate},
                {paymentId}, {rendered.Subject}, {rendered.Body}, {status}, {reason}, 0, {now}, {now})
            ON CONFLICT (source_message_id, recipient_owner_id, template) DO NOTHING
            """, ct);
    }
}
