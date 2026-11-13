using Ledgerline.BankSimulator.Infrastructure;
using Ledgerline.BuildingBlocks.Web.Problems;

namespace Ledgerline.BankSimulator.Features;

public static class LookupEndpoint
{
    public static async Task<IResult> HandleAsync(string idempotencyKey, BankDbContext db, CancellationToken ct)
    {
        var operation = await db.Operations.FindAsync([idempotencyKey], ct);
        return operation is null ? ProblemResults.NotFound() : Results.Ok(new
        {
            operation.Kind, operation.HttpStatus, body = operation.ResponseBody
        });
    }
}
