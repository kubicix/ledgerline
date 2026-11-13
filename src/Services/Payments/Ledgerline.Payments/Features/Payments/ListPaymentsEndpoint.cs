using System.Text.Json;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Pagination;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Sagas;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.Payments;

internal static class ListPaymentsEndpoint
{
    internal static async Task<IResult> HandleAsync(Guid accountId, string? status, int? limit, string? cursor,
        PaymentsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var owner = user.Id;
        var admin = user.IsInRole("admin");
        if (!await db.AccountRefs.AnyAsync(x => x.AccountId == accountId && (admin || x.OwnerId == owner), ct)) return ProblemResults.NotFound();
        var size = limit ?? 20;
        if (size is < 1 or > 100 || !Decode(cursor, out var before)) return Invalid("Use limit 1 to 100 and a valid cursor.");
        PaymentStatus? filter = null;
        if (status is not null)
        {
            if (!Enum.TryParse<PaymentStatus>(status, out var parsed) || parsed.ToString() != status) return Invalid("Unknown payment status.");
            filter = parsed;
        }
        var query = db.Payments.AsNoTracking().Where(x => x.SourceAccountId == accountId || x.DestinationAccountId == accountId);
        if (filter is { } state) query = query.Where(x => x.Status == state);
        if (before is not null) query = query.Where(x => x.CreatedAt < before.At || x.CreatedAt == before.At && x.Id.CompareTo(before.Id) < 0);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(size + 1).ToListAsync(ct);
        var next = rows.Count > size ? WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Cursor(rows[size - 1].CreatedAt, rows[size - 1].Id))) : null;
        var items = new List<PaymentResponse>();
        foreach (var row in rows.Take(size)) items.Add(await PaymentResponse.FromAsync(row, db, ct));
        return Results.Ok(new Page<PaymentResponse>(items, next));
    }

    private static IResult Invalid(string detail) => ProblemResults.Validation(new Dictionary<string, string[]> { ["query"] = [detail] });

    private static bool Decode(string? value, out Cursor? cursor)
    {
        cursor = null;
        if (value is null) return true;
        try
        {
            cursor = JsonSerializer.Deserialize<Cursor>(WebEncoders.Base64UrlDecode(value));
            return cursor is not null && cursor.Id != Guid.Empty && cursor.At.Offset == TimeSpan.Zero;
        }
        catch (FormatException) { return false; }
        catch (JsonException) { return false; }
    }

    private sealed record Cursor(DateTimeOffset At, Guid Id);
}
