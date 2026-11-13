using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Payments.Sagas;
using Ledgerline.Payments.Features.Refunds;

namespace Ledgerline.Payments.Features.LedgerReplies;

public sealed class LedgerOperationRejectedHandler(SagaRunner runner, RefundRunner refunds) : IMessageHandler<LedgerOperationRejected>
{
    public async Task HandleAsync(LedgerOperationRejected message, MessageContext context, CancellationToken ct)
    {
        var input = new SagaInput.LedgerRejected(message.OperationId, message.ReasonCode);
        if (message.Kind == LedgerOperationKind.RefundFunds && await refunds.TryApplyAsync(message.PaymentId, message.OperationId, input, context, ct)) return;
        await runner.ApplyAsync(message.PaymentId, input, context, ct);
    }
}
