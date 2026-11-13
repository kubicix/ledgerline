using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Accounts.Features.Accounts;

internal static class ListAccountsEndpoint
{
    internal static async Task<IResult> HandleAsync(AccountsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var owner = user.Id;
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.OwnerId == owner).OrderBy(a => a.OpenedAt).ThenBy(a => a.Id).ToListAsync(ct);
        var items = new List<AccountResponse>();
        foreach (var account in accounts)
        {
            var held = await db.Holds.Where(h => h.AccountId == account.Id && h.Status == "Active").SumAsync(h => h.AmountMinor, ct);
            items.Add(AccountResponse.From(account, held));
        }
        return Results.Ok(items);
    }
}
