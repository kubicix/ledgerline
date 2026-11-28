using System.Globalization;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Reporting.Infrastructure;
using Ledgerline.Reporting.Projections;
using MongoDB.Driver;

namespace Ledgerline.Reporting.Features;

/// <summary>
/// <c>GET /merchants/{merchantAccountId}/daily-totals?from=&amp;to=</c>: the merchant account's daily totals, ascending by
/// UTC day and then currency, for the owner of that account or an admin. <c>from</c> and <c>to</c> are inclusive
/// days (<c>yyyy-MM-dd</c>). The response is a list, like Accounts' <c>GET /accounts</c>.
/// </summary>
internal static class DailyTotalsEndpoint
{
    internal static async Task<IResult> HandleAsync(Guid merchantAccountId, DateOnly? from, DateOnly? to,
        MongoCollections collections, ICurrentUser user, CancellationToken ct)
    {
        if (!await ReportingEndpoints.CanReadAccountAsync(collections, merchantAccountId, user, ct)) return ProblemResults.NotFound();
        if (from > to) return ProblemResults.Validation(new Dictionary<string, string[]> { ["range"] = ["from must not be after to."] });

        var filters = Builders<MerchantDailyTotalsDocument>.Filter;
        var filter = filters.Eq(x => x.MerchantAccountId, merchantAccountId.ToString());
        // yyyy-MM-dd strings sort in date order, so the day range is a string range.
        if (from is not null) filter &= filters.Gte(x => x.Day, Day(from.Value));
        if (to is not null) filter &= filters.Lte(x => x.Day, Day(to.Value));
        var rows = await collections.MerchantDailyTotals.Find(filter)
            .Sort(Builders<MerchantDailyTotalsDocument>.Sort.Ascending(x => x.Day).Ascending(x => x.Currency)).ToListAsync(ct);
        return Results.Ok(rows.Select(DailyTotalResponse.From).ToArray());
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>One merchant, UTC day and currency. The deduplication window (<c>appliedMessageIds</c>) is not exposed.</summary>
public sealed record DailyTotalResponse(Guid MerchantAccountId, DateOnly Day, string Currency, long CapturedMinor, long RefundedMinor,
    long CaptureCount, long RefundCount)
{
    internal static DailyTotalResponse From(MerchantDailyTotalsDocument totals) => new(Guid.Parse(totals.MerchantAccountId),
        DateOnly.ParseExact(totals.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture), totals.Currency, totals.CapturedMinor,
        totals.RefundedMinor, totals.CaptureCount, totals.RefundCount);
}
