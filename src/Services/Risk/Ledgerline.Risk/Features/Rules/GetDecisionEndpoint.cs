using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Risk.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Risk.Features.Rules;

public static class GetDecisionEndpoint
{
    public static async Task<IResult> HandleAsync(Guid paymentId, RiskDbContext db, CancellationToken ct)
    {
        var decision = await db.Decisions.AsNoTracking().SingleOrDefaultAsync(x => x.PaymentId == paymentId, ct);
        return decision is null ? ProblemResults.NotFound() : Results.Ok(new
        {
            decision.PaymentId, decision.CustomerId, decision.Outcome, decision.RuleCodes, decision.EvaluatedAt
        });
    }
}
