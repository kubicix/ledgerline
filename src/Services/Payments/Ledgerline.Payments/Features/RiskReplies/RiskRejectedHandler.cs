using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Risk;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Features.RiskReplies;

public sealed class RiskRejectedHandler(SagaRunner runner) : IMessageHandler<RiskRejected>
{
    public Task HandleAsync(RiskRejected message, MessageContext context, CancellationToken ct) =>
        runner.ApplyAsync(message.PaymentId, new SagaInput.RiskRejected(string.Join(',', message.RuleCodes)), context, ct);
}
