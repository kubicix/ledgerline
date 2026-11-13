using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Accounts.Features.Ledger;

public static class TrialBalanceEndpoint
{
    public static void MapTrialBalanceEndpoint(this WebApplication app) =>
        app.MapGet("/ledger/trial-balance", HandleAsync).RequireAuthorization(Policies.Admin);

    private static async Task<IResult> HandleAsync(AccountsDbContext db, IClock clock, CancellationToken ct)
    {
        var currencies = await db.Accounts.AsNoTracking().GroupBy(x => x.Currency).OrderBy(x => x.Key).Select(group => new
        {
            currency = group.Key, sumOfBalancesMinor = group.Sum(x => x.BalanceMinor), accounts = group.Count(),
            journalEntries = db.JournalEntries.Count(entry => entry.Currency == group.Key)
        }).ToListAsync(ct);
        return Results.Ok(new { asOf = clock.UtcNow, currencies });
    }
}
