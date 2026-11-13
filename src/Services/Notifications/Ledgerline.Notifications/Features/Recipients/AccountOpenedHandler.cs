using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Notifications.Features.Recipients;

public sealed class AccountOpenedHandler(NotificationsDbContext db) : IMessageHandler<AccountOpened>
{
    public async Task HandleAsync(AccountOpened message, MessageContext context, CancellationToken ct)
    {
        if (message.OwnerId is not { } owner) return;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO recipients (account_id, owner_id, email)
            VALUES ({message.AccountId}, {owner}, {message.ContactEmail ?? ""})
            ON CONFLICT (account_id) DO NOTHING
            """, ct);
    }
}
