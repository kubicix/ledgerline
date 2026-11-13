using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Payments;

namespace Ledgerline.Notifications.Features.PaymentEvents;

public sealed class PaymentAuthorizedHandler(NotificationPlanner planner) : IMessageHandler<PaymentAuthorized>
{
    public Task HandleAsync(PaymentAuthorized message, MessageContext context, CancellationToken ct) =>
        planner.PlanAsync(message.SourceAccountId!.Value, message.CustomerId, "payment-authorized", message.PaymentId,
            new(message.Kind, message.AmountMinor, message.Currency), context, ct);
}
