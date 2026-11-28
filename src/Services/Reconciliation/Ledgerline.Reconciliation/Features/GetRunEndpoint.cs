using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Reconciliation.Infrastructure;
using Ledgerline.Reconciliation.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Reconciliation.Features;

public sealed record RunResponse(Guid RunId, DateOnly BusinessDate, string Status, int Matched, int Mismatched, bool InputsIncomplete,
    string? Error, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt)
{
    internal static RunResponse From(ReconciliationRun run) => new(run.Id, run.BusinessDate, run.Status, run.Matched, run.Mismatched,
        run.InputsIncomplete, run.Error, run.StartedAt, run.CompletedAt);
}

internal static class GetRunEndpoint
{
    internal static async Task<IResult> HandleAsync(string businessDate, ReconciliationDbContext db, CancellationToken ct)
    {
        if (!BusinessDates.TryParse(businessDate, out var date)) return BusinessDates.Invalid("businessDate", "Use a business date as yyyy-MM-dd.");
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.BusinessDate == date, ct);
        return run is null ? ProblemResults.NotFound() : Results.Ok(RunResponse.From(run));
    }
}
