using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Payments.Sagas;
using Ledgerline.Payments.Features.Refunds;

namespace Ledgerline.Payments.Features.LedgerReplies;

public sealed class LedgerOperationSucceededHandler(SagaRunner runner, RefundRunner refunds) : IMessageHandler<LedgerOperationSucceeded>
{
    public async Task HandleAsync(LedgerOperationSucceeded message, MessageContext context, CancellationToken ct)
    {
        var input = new SagaInput.LedgerSucceeded(message.OperationId);
        if (message.Kind == LedgerOperationKind.RefundFunds && await refunds.TryApplyAsync(message.PaymentId, message.OperationId, input, context, ct)) return;
        await runner.ApplyAsync(message.PaymentId, input, context, ct);
    }
}
