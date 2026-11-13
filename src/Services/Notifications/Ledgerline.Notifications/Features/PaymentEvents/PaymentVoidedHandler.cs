using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Payments;

namespace Ledgerline.Notifications.Features.PaymentEvents;

public sealed class PaymentVoidedHandler(NotificationPlanner planner) : IMessageHandler<PaymentVoided>
{
    public Task HandleAsync(PaymentVoided message, MessageContext context, CancellationToken ct) =>
        planner.PlanAsync(message.SourceAccountId!.Value, message.CustomerId, "payment-voided", message.PaymentId,
            new(message.Kind, message.AmountMinor, message.Currency, message.Reason), context, ct);
}
