using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.AspNetCore.Http;

namespace Ledgerline.BuildingBlocks.Web.Idempotency;

public sealed record IdempotentReplay(string RequestHash, IResult Response);

public static class IdempotentCreate
{
    public static async Task<IResult> ExecuteAsync(string requestHash, Func<Task<IResult>> create,
        Func<Task<IdempotentReplay?>> findExisting, Func<Exception, bool> isUniqueViolation)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(findExisting);
        ArgumentNullException.ThrowIfNull(isUniqueViolation);
        var existing = await findExisting();
        if (existing is not null) return Replay(existing, requestHash);
        try { return await create(); }
        catch (Exception error) when (isUniqueViolation(error))
        {
            existing = await findExisting();
            if (existing is null) throw;
            return Replay(existing, requestHash);
        }
    }

    private static IResult Replay(IdempotentReplay existing, string hash) =>
        existing.RequestHash == hash ? existing.Response : ProblemResults.IdempotencyKeyReused();
}
