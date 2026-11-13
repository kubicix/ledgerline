using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;

namespace Ledgerline.Accounts.Features.LedgerCommands;

public sealed class RefundFundsHandler(LedgerOperationRunner runner) : IMessageHandler<RefundFunds>
{
    public Task HandleAsync(RefundFunds message, MessageContext context, CancellationToken ct) =>
        runner.RunAsync(message.OperationId, message.PaymentId, LedgerOperationKind.RefundFunds,
            () => Entries.Refund(message.OperationId, message.PaymentId, message.MerchantAccountId,
                message.CustomerAccountId, Money.Create(message.AmountMinor, message.Currency)), context, ct);
}
