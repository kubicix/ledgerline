using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.Notifications.Infrastructure;
using Ledgerline.Notifications.Templates;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Notifications.Features.PaymentEvents;

public sealed class NotificationPlanner(NotificationsDbContext db, ConsumerOptions options, IClock clock)
{
    public async Task PlanAsync(Guid accountId, Guid? knownOwner, string template, Guid paymentId,
        TemplateData data, MessageContext context, CancellationToken ct)
    {
        var recipient = await db.Recipients.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, ct);
        var unknown = recipient is null || string.IsNullOrWhiteSpace(recipient.Email);
        if (unknown && context.Attempt < options.MaxAttempts) throw new InvalidOperationException("Notification recipient is not available yet.");
        var owner = recipient?.OwnerId ?? knownOwner ?? Guid.Empty;
        var status = unknown ? "Skipped" : "Pending";
        var reason = unknown ? "RecipientUnknown" : null;
        var rendered = new RenderedTemplate("", "");
        try { rendered = TemplateRenderer.Render(template, data); }
        catch (ArgumentException) { status = "Failed"; reason = "TemplateError"; }
        var now = clock.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications (id, source_message_id, recipient_owner_id, recipient_email, template,
                payment_id, subject, body, status, reason, attempts, next_attempt_at, created_at)
            VALUES ({Guid.NewGuid()}, {context.MessageId}, {owner}, {recipient?.Email ?? ""}, {template},
                {paymentId}, {rendered.Subject}, {rendered.Body}, {status}, {reason}, 0, {now}, {now})
            ON CONFLICT (source_message_id, recipient_owner_id, template) DO NOTHING
            """, ct);
    }
}
