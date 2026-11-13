using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.AccountReplica;

public sealed class AccountOpenedHandler(PaymentsDbContext db) : IMessageHandler<AccountOpened>
{
    public async Task HandleAsync(AccountOpened message, MessageContext context, CancellationToken ct)
    {
        if (message.OwnerId is null) return;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO account_refs (account_id, owner_id, type, currency, status)
            VALUES ({message.AccountId}, {message.OwnerId.Value}, {message.Type}, {message.Currency}, 'Active')
            ON CONFLICT (account_id) DO NOTHING
            """, ct);
    }
}
