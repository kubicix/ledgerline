using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;

namespace Ledgerline.Accounts.Features.LedgerCommands;

public sealed class ReleaseFundsHandler(LedgerOperationRunner runner, Holds holds) : IMessageHandler<ReleaseFunds>
{
    public Task HandleAsync(ReleaseFunds message, MessageContext context, CancellationToken ct) =>
        runner.RunAsync(message.OperationId, message.PaymentId, LedgerOperationKind.ReleaseFunds,
            () => holds.ReleaseAsync(message.OperationId, message.PaymentId, ct), context, ct);
}
