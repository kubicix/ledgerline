using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Payments;

namespace Ledgerline.Notifications.Features.PaymentEvents;

public sealed class PaymentFailedHandler(NotificationPlanner planner) : IMessageHandler<PaymentFailed>
{
    public Task HandleAsync(PaymentFailed message, MessageContext context, CancellationToken ct) =>
        planner.PlanAsync(message.SourceAccountId ?? message.DestinationAccountId, message.CustomerId, "payment-failed", message.PaymentId,
            new(message.Kind, message.AmountMinor, message.Currency, message.ReasonCode), context, ct);
}
