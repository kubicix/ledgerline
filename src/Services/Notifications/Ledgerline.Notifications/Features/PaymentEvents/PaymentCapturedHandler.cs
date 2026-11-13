using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Payments;

namespace Ledgerline.Notifications.Features.PaymentEvents;

public sealed class PaymentCapturedHandler(NotificationPlanner planner) : IMessageHandler<PaymentCaptured>
{
    public async Task HandleAsync(PaymentCaptured message, MessageContext context, CancellationToken ct)
    {
        var data = new Templates.TemplateData(message.Kind, message.CapturedMinor, message.Currency);
        await planner.PlanAsync(message.SourceAccountId!.Value, message.CustomerId, "payment-captured", message.PaymentId, data, context, ct);
        await planner.PlanAsync(message.DestinationAccountId, null, "merchant-payment-captured", message.PaymentId, data, context, ct);
    }
}
