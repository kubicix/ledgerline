using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Reconciliation.Infrastructure;
using Ledgerline.Reconciliation.Matching;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Reconciliation.Features;

public sealed record ItemResponse(string Code, string Reference, long? BankAmountMinor, long? LedgerAmountMinor, string? Currency);

internal static class ListItemsEndpoint
{
    internal static async Task<IResult> HandleAsync(string businessDate, string? code, ReconciliationDbContext db, CancellationToken ct)
    {
        if (!BusinessDates.TryParse(businessDate, out var date)) return BusinessDates.Invalid("businessDate", "Use a business date as yyyy-MM-dd.");
        // Ordinal and case-sensitive, as the codes are stored; Enum.TryParse would also take a number.
        if (code is not null && !Enum.GetNames<MismatchCode>().Contains(code, StringComparer.Ordinal)) return BusinessDates.Invalid("code", "Unknown mismatch code.");
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.BusinessDate == date, ct);
        if (run is null) return ProblemResults.NotFound();
        var query = db.Items.AsNoTracking().Where(x => x.RunId == run.Id);
        if (code is not null) query = query.Where(x => x.Code == code);
        // A run has few items and the spec has no paging here. Ordered in memory so the order is ordinal, not the database collation's.
        var items = await query.ToListAsync(ct);
        return Results.Ok(items.OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Reference, StringComparer.Ordinal)
            .Select(x => new ItemResponse(x.Code, x.Reference, x.BankAmountMinor, x.LedgerAmountMinor, x.Currency)).ToList());
    }
}
