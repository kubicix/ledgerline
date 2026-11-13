using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Accounts.IntegrationTests;

internal static class TestAccounts
{
    internal static async Task<Guid> OpenWalletAsync(AccountsDbContext db, Guid ownerId, string currency, string type = "CustomerWallet")
    {
        var id = Ids.New();
        var now = DateTimeOffset.UtcNow;
        db.Accounts.Add(new Account { Id = id, OwnerId = ownerId, Type = type, Currency = currency, Status = "Active", Version = 1, OpenedAt = now });
        db.Events.Add(new LedgerEvent
        {
            StreamId = id, Version = 1, Type = "account-opened.v1", OccurredAt = now,
            Data = EnvelopeSerializer.ToPayload(new { OwnerId = ownerId, Type = type, SystemCode = (string?)null, Currency = currency, ContactEmail = (string?)null }),
            Metadata = EnvelopeSerializer.ToPayload(new { CorrelationId = id, CausationId = (Guid?)null, OperationId = (Guid?)null })
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
