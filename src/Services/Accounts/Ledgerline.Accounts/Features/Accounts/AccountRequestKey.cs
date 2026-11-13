using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks.Web.Idempotency;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Accounts.Features.Accounts;

internal static class AccountRequestKey
{
    internal static Task LockAsync(AccountsDbContext db, Guid caller, string key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({$"accounts-key:{caller}:{key}"}, 0))", ct);

    internal static IResult NoContent(Guid account) => new StatusResult(account);

    private sealed record Identity(Guid Id) : IIdempotentResource;
    private sealed class StatusResult(Guid account) : IResult, IStatusCodeHttpResult, IValueHttpResult
    {
        public int? StatusCode => 204;
        public object? Value => new Identity(account);
        public Task ExecuteAsync(HttpContext context) { context.Response.StatusCode = 204; return Task.CompletedTask; }
    }
}
