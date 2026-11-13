using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.Payments;

internal static class GetPaymentEndpoint
{
    internal static async Task<IResult> HandleAsync(Guid id, PaymentsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (payment is null) return ProblemResults.NotFound();
        var ownsDestination = await db.AccountRefs.AnyAsync(x => x.AccountId == payment.DestinationAccountId && x.OwnerId == user.Id, ct);
        if (payment.CustomerId != user.Id && !ownsDestination && !user.IsInRole("admin")) return ProblemResults.NotFound();
        return Results.Ok(await PaymentResponse.FromAsync(payment, db, ct));
    }
}
