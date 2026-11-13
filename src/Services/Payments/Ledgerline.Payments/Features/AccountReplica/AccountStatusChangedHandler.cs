using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.AccountReplica;

public sealed class AccountStatusChangedHandler(PaymentsDbContext db) : IMessageHandler<AccountStatusChanged>
{
    public async Task HandleAsync(AccountStatusChanged message, MessageContext context, CancellationToken ct)
    {
        var updated = await db.AccountRefs.Where(x => x.AccountId == message.AccountId && x.StatusVersion < message.Version)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, message.Status).SetProperty(x => x.StatusVersion, message.Version), ct);
        if (updated == 0 && !await db.AccountRefs.AnyAsync(x => x.AccountId == message.AccountId, ct))
            throw new InvalidOperationException("Account status arrived before its account replica; retry after account-opened.");
    }
}
