using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Risk;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Features.RiskReplies;

public sealed class RiskApprovedHandler(SagaRunner runner) : IMessageHandler<RiskApproved>
{
    public Task HandleAsync(RiskApproved message, MessageContext context, CancellationToken ct) =>
        runner.ApplyAsync(message.PaymentId, new SagaInput.RiskApproved(), context, ct);
}
