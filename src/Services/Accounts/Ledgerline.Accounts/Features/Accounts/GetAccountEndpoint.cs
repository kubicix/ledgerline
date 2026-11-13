using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Accounts.Features.Accounts;

internal static class GetAccountEndpoint
{
    internal static async Task<IResult> HandleAsync(Guid id, AccountsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var owner = user.Id;
        var admin = user.IsInRole("admin");
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && (admin || a.OwnerId == owner), ct);
        if (account is null) return ProblemResults.NotFound();
        var held = await db.Holds.Where(h => h.AccountId == id && h.Status == "Active").SumAsync(h => h.AmountMinor, ct);
        return Results.Ok(AccountResponse.From(account, held));
    }
}
