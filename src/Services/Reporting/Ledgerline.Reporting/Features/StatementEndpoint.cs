using System.Text.Json;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Pagination;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Reporting.Infrastructure;
using Ledgerline.Reporting.Projections;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Driver;

namespace Ledgerline.Reporting.Features;

public static class ReportingEndpoints
{
    /// <summary>The read-only API of reporting.md section 5. The gateway serves it under <c>/api/reporting</c>.</summary>
    public static WebApplication MapReportingEndpoints(this WebApplication app)
    {
        app.MapGet("/accounts/{accountId:guid}/statement", StatementEndpoint.HandleAsync).RequireAuthorization();
        app.MapGet("/payments/{paymentId:guid}/timeline", TimelineEndpoint.HandleAsync).RequireAuthorization();
        app.MapGet("/merchants/{merchantAccountId:guid}/daily-totals", DailyTotalsEndpoint.HandleAsync).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// The caller owns the account, or is an admin, according to Reporting's own <c>account_owners</c>. An account not
    /// known yet is denied like a foreign one, so both give 404.
    /// </summary>
    internal static async Task<bool> CanReadAccountAsync(MongoCollections collections, Guid accountId, ICurrentUser user, CancellationToken ct)
    {
        var id = accountId.ToString();
        var known = await collections.AccountOwners.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return known is not null && (user.IsInRole("admin") || known.OwnerId == user.Id.ToString());
    }
}

/// <summary>
/// <c>GET /accounts/{accountId}/statement?from=&amp;to=&amp;limit=&amp;cursor=</c>: statement lines newest first, by
/// <c>postedAt</c> and then <c>_id</c> descending, so lines posted in the same millisecond keep a fixed order across
/// pages. <c>from</c> and <c>to</c> are inclusive bounds on <c>postedAt</c>. Limit and cursor follow Accounts'
/// <c>GET /accounts/{id}/entries</c>.
/// </summary>
internal static class StatementEndpoint
{
    internal static async Task<IResult> HandleAsync(Guid accountId, DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor,
        MongoCollections collections, ICurrentUser user, CancellationToken ct)
    {
        if (!await ReportingEndpoints.CanReadAccountAsync(collections, accountId, user, ct)) return ProblemResults.NotFound();
        var size = limit ?? 20;
        if (size is < 1 or > 100 || !StatementCursor.TryDecode(cursor, out var before))
            return ProblemResults.Validation(new Dictionary<string, string[]> { ["pagination"] = ["Use limit 1 to 100 and a valid cursor."] });
        if (from > to) return ProblemResults.Validation(new Dictionary<string, string[]> { ["range"] = ["from must not be after to."] });

        var filters = Builders<AccountStatementDocument>.Filter;
        var filter = filters.Eq(x => x.AccountId, accountId.ToString());
        if (from is not null) filter &= filters.Gte(x => x.PostedAt, from.Value.UtcDateTime);
        if (to is not null) filter &= filters.Lte(x => x.PostedAt, to.Value.UtcDateTime);
        if (before is not null)
        {
            var at = before.At.UtcDateTime;
            filter &= filters.Or(filters.Lt(x => x.PostedAt, at), filters.And(filters.Eq(x => x.PostedAt, at), filters.Lt(x => x.Id, before.Id)));
        }
        var rows = await collections.AccountStatements.Find(filter)
            .Sort(Builders<AccountStatementDocument>.Sort.Descending(x => x.PostedAt).Descending(x => x.Id))
            .Limit(size + 1).ToListAsync(ct);
        var next = rows.Count > size ? new StatementCursor(Utc(rows[size - 1].PostedAt), rows[size - 1].Id).Encode() : null;
        return Results.Ok(new Page<StatementLineResponse>(rows.Take(size).Select(StatementLineResponse.From).ToArray(), next));
    }

    internal static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc), TimeSpan.Zero);
}

/// <summary>A statement line, shaped like an item of Accounts' <c>GET /accounts/{id}/entries</c>.</summary>
public sealed record StatementLineResponse(Guid JournalEntryId, string EntryType, string Direction, long AmountMinor,
    string Currency, long BalanceAfterMinor, Guid PaymentId, DateTimeOffset PostedAt)
{
    internal static StatementLineResponse From(AccountStatementDocument line) => new(Guid.Parse(line.JournalEntryId), line.EntryType,
        line.Direction, line.AmountMinor, line.Currency, line.BalanceAfterMinor, Guid.Parse(line.PaymentId), StatementEndpoint.Utc(line.PostedAt));
}

/// <summary>The last line of a page: its <c>postedAt</c> and <c>_id</c>, the two sort keys. Opaque to clients.</summary>
internal sealed record StatementCursor(DateTimeOffset At, string Id)
{
    internal string Encode() => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(this));

    internal static bool TryDecode(string? value, out StatementCursor? cursor)
    {
        cursor = null;
        if (value is null) return true;
        try
        {
            cursor = JsonSerializer.Deserialize<StatementCursor>(WebEncoders.Base64UrlDecode(value));
            return cursor is not null && !string.IsNullOrEmpty(cursor.Id) && cursor.At.Offset == TimeSpan.Zero;
        }
        catch (FormatException) { return false; }
        catch (JsonException) { return false; }
    }
}
