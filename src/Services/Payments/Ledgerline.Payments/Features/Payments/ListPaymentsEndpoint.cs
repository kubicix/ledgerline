using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Pagination;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Sagas;
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
        if (size is < 1 or > 100 || !ResourceCursor.TryDecode(cursor, out var before)) return Invalid("Use limit 1 to 100 and a valid cursor.");
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
        var next = rows.Count > size ? new ResourceCursor(rows[size - 1].CreatedAt, rows[size - 1].Id).Encode() : null;
        var items = new List<PaymentResponse>();
        foreach (var row in rows.Take(size)) items.Add(await PaymentResponse.FromAsync(row, db, ct));
        return Results.Ok(new Page<PaymentResponse>(items, next));
    }

    private static IResult Invalid(string detail) => ProblemResults.Validation(new Dictionary<string, string[]> { ["query"] = [detail] });

}
