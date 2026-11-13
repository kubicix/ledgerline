using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.AccountReplica;

public sealed class AccountStatusChangedHandler(PaymentsDbContext db) : IMessageHandler<AccountStatusChanged>
{
    public async Task HandleAsync(AccountStatusChanged message, MessageContext context, CancellationToken ct)
    {
        var updated = await db.AccountRefs.Where(x => x.AccountId == message.AccountId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, message.Status), ct);
        if (updated == 0) throw new InvalidOperationException("Account status arrived before its account replica; retry after account-opened.");
    }
}
