using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Features.LedgerReplies;

public sealed class LedgerOperationRejectedHandler(SagaRunner runner) : IMessageHandler<LedgerOperationRejected>
{
    public Task HandleAsync(LedgerOperationRejected message, MessageContext context, CancellationToken ct) =>
        runner.ApplyAsync(message.PaymentId, new SagaInput.LedgerRejected(message.OperationId, message.ReasonCode), context, ct);
}
