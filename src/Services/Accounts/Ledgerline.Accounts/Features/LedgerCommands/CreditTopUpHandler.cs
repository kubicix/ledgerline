using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;

namespace Ledgerline.Accounts.Features.LedgerCommands;

public sealed class CreditTopUpHandler(LedgerOperationRunner runner) : IMessageHandler<CreditTopUp>
{
    public Task HandleAsync(CreditTopUp message, MessageContext context, CancellationToken ct) =>
        runner.RunAsync(message.OperationId, message.PaymentId, LedgerOperationKind.CreditTopUp,
            () => Entries.TopUp(message.OperationId, message.PaymentId, message.AccountId, Money.Create(message.AmountMinor, message.Currency)), context, ct);
}
