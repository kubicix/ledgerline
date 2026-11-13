using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Pagination;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Payments.Features.Payments;
using Ledgerline.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.Refunds;

internal static class ListRefundsEndpoint
{
    internal static async Task<IResult> HandleAsync(Guid id, int? limit, string? cursor, PaymentsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (payment is null) return ProblemResults.NotFound();
        if (payment.CustomerId != user.Id && !user.IsInRole("admin") &&
            !await db.AccountRefs.AnyAsync(x => x.AccountId == payment.DestinationAccountId && x.OwnerId == user.Id, ct)) return ProblemResults.NotFound();
        var size = limit ?? 20;
        if (size is < 1 or > 100 || !ResourceCursor.TryDecode(cursor, out var before))
            return ProblemResults.Validation(new Dictionary<string, string[]> { ["pagination"] = ["Use limit 1 to 100 and a valid cursor."] });
        var query = db.Refunds.AsNoTracking().Where(x => x.PaymentId == id);
        if (before is not null) query = query.Where(x => x.CreatedAt < before.At || x.CreatedAt == before.At && x.Id.CompareTo(before.Id) < 0);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(size + 1).ToListAsync(ct);
        var next = rows.Count > size ? new ResourceCursor(rows[size - 1].CreatedAt, rows[size - 1].Id).Encode() : null;
        return Results.Ok(new Page<RefundResponse>(rows.Take(size).Select(RefundResponse.From).ToArray(), next));
    }
}
