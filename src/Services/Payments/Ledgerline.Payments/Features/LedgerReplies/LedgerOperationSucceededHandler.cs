using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Features.LedgerReplies;

public sealed class LedgerOperationSucceededHandler(SagaRunner runner) : IMessageHandler<LedgerOperationSucceeded>
{
    public Task HandleAsync(LedgerOperationSucceeded message, MessageContext context, CancellationToken ct) =>
        runner.ApplyAsync(message.PaymentId, new SagaInput.LedgerSucceeded(message.OperationId), context, ct);
}
