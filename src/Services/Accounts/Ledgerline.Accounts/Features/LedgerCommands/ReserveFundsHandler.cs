using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;

namespace Ledgerline.Accounts.Features.LedgerCommands;

public sealed class ReserveFundsHandler(LedgerOperationRunner runner, Holds holds) : IMessageHandler<ReserveFunds>
{
    public Task HandleAsync(ReserveFunds message, MessageContext context, CancellationToken ct) =>
        runner.RunAsync(message.OperationId, message.PaymentId, LedgerOperationKind.ReserveFunds,
            () => holds.ReserveAsync(message.OperationId, message.PaymentId, message.AccountId, Money.Create(message.AmountMinor, message.Currency), ct), context, ct);
}
