using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Web.Auth;

namespace Ledgerline.Accounts.Features.Accounts;

internal static class UnfreezeEndpoint
{
    internal static Task<IResult> HandleAsync(Guid id, HttpContext http, ICurrentUser user, AccountsDbContext db, IOutbox outbox, IClock clock, CancellationToken ct) =>
        FreezeEndpoint.ChangeAsync(id, "Active", http, user, db, outbox, clock, ct);
}
