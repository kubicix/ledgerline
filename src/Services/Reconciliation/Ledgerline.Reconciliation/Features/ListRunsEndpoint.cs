using System.Text;
using Ledgerline.BuildingBlocks.Web.Pagination;
using Ledgerline.Reconciliation.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Reconciliation.Features;

internal static class ListRunsEndpoint
{
    internal static async Task<IResult> HandleAsync(int? limit, string? cursor, ReconciliationDbContext db, CancellationToken ct)
    {
        var size = limit ?? 20;
        if (size is < 1 or > 100 || !TryDecode(cursor, out var before)) return BusinessDates.Invalid("query", "Use limit 1 to 100 and a valid cursor.");
        var query = db.Runs.AsNoTracking();
        if (before is { } last) query = query.Where(x => x.BusinessDate < last);
        var rows = await query.OrderByDescending(x => x.BusinessDate).Take(size + 1).ToListAsync(ct);
        var next = rows.Count > size ? Encode(rows[size - 1].BusinessDate) : null;
        return Results.Ok(new Page<RunResponse>(rows.Take(size).Select(RunResponse.From).ToList(), next));
    }

    /// <summary>The cursor is the last business date of the page, as URL-safe base64 of its text. Business dates are unique, so it is a total order.</summary>
    private static string Encode(DateOnly lastDate) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(BusinessDates.ToText(lastDate)));

    private static bool TryDecode(string? cursor, out DateOnly? date)
    {
        date = null;
        if (cursor is null) return true;
        try
        {
            if (!BusinessDates.TryParse(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor)), out var parsed)) return false;
            date = parsed;
            return true;
        }
        catch (FormatException) { return false; }
    }
}
