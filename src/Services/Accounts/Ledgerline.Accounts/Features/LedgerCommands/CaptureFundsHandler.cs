using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;

namespace Ledgerline.Accounts.Features.LedgerCommands;

public sealed class CaptureFundsHandler(LedgerOperationRunner runner, Holds holds) : IMessageHandler<CaptureFunds>
{
    public Task HandleAsync(CaptureFunds message, MessageContext context, CancellationToken ct) =>
        runner.RunAsync(message.OperationId, message.PaymentId, LedgerOperationKind.CaptureFunds,
            () => holds.CaptureAsync(message.OperationId, message.PaymentId, message.MerchantAccountId, message.CaptureAmountMinor, ct), context, ct);
}
