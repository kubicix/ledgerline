using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Payments;

namespace Ledgerline.Notifications.Features.PaymentEvents;

public sealed class PaymentCompletedHandler(NotificationPlanner planner) : IMessageHandler<PaymentCompleted>
{
    public async Task HandleAsync(PaymentCompleted message, MessageContext context, CancellationToken ct)
    {
        var account = message.Kind == "Transfer" ? message.SourceAccountId!.Value : message.DestinationAccountId;
        var data = new Templates.TemplateData(message.Kind, message.AmountMinor, message.Currency);
        await planner.PlanAsync(account, message.CustomerId, "payment-completed", message.PaymentId, data, context, ct);
        if (message.Kind == "Transfer")
            await planner.PlanAsync(message.DestinationAccountId, null, "transfer-received", message.PaymentId, data, context, ct);
    }
}
