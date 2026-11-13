using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Payments;

namespace Ledgerline.Notifications.Features.PaymentEvents;

public sealed class PaymentRefundedHandler(NotificationPlanner planner) : IMessageHandler<PaymentRefunded>
{
    public Task HandleAsync(PaymentRefunded message, MessageContext context, CancellationToken ct) =>
        planner.PlanAsync(message.SourceAccountId!.Value, message.CustomerId, "payment-refunded", message.PaymentId,
            new(message.Kind, message.RefundAmountMinor, message.Currency), context, ct);
}
