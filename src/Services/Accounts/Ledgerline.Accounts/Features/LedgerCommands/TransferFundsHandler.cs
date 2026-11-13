using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;

namespace Ledgerline.Accounts.Features.LedgerCommands;

public sealed class TransferFundsHandler(LedgerOperationRunner runner) : IMessageHandler<TransferFunds>
{
    public Task HandleAsync(TransferFunds message, MessageContext context, CancellationToken ct) =>
        runner.RunAsync(message.OperationId, message.PaymentId, LedgerOperationKind.TransferFunds,
            () => Entries.Transfer(message.OperationId, message.PaymentId, message.FromAccountId, message.ToAccountId,
                Money.Create(message.AmountMinor, message.Currency)), context, ct);
}
