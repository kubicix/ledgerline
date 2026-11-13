using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Idempotency;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.EntityFrameworkCore;
using StatusMessage = Ledgerline.Contracts.Accounts.AccountStatusChanged;
using StatusEvent = Ledgerline.Accounts.Ledger.AccountStatusChanged;

namespace Ledgerline.Accounts.Features.Accounts;

internal static class FreezeEndpoint
{
    internal static Task<IResult> HandleAsync(Guid id, HttpContext http, ICurrentUser user, AccountsDbContext db, IOutbox outbox, IClock clock, CancellationToken ct) =>
        ChangeAsync(id, "Frozen", http, user, db, outbox, clock, ct);

    internal static async Task<IResult> ChangeAsync(Guid id, string status, HttpContext http, ICurrentUser user, AccountsDbContext db, IOutbox outbox, IClock clock, CancellationToken ct)
    {
        var key = (string)http.Items[IdempotencyEndpointFilter.KeyItem]!;
        var hash = (string)http.Items[IdempotencyEndpointFilter.HashItem]!;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AccountRequestKey.LockAsync(db, user.Id, key, ct);
        var existing = await db.StatusRequests.FindAsync([user.Id, key], ct);
        if (existing is not null) return existing.RequestHash == hash ? AccountRequestKey.NoContent(existing.AccountId) : ProblemResults.IdempotencyKeyReused();
        if (await db.Accounts.AnyAsync(x => x.OwnerId == user.Id && x.IdempotencyKey == key, ct)) return ProblemResults.IdempotencyKeyReused();
        var account = await db.Accounts.FromSqlInterpolated($"SELECT * FROM accounts WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (account is null) return ProblemResults.NotFound();
        if (account.Type == "System") return ProblemResults.InvalidState("System accounts cannot be frozen or unfrozen.");
        var now = clock.UtcNow;
        if (account.Status != status) AppendChange(account, status, db, outbox, now);
        db.StatusRequests.Add(new AccountStatusRequest { CallerId = user.Id, Key = key, RequestHash = hash, AccountId = id, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return AccountRequestKey.NoContent(id);
    }

    private static void AppendChange(Account account, string status, AccountsDbContext db, IOutbox outbox, DateTimeOffset now)
    {
        account.Status = status;
        account.Version = checked(account.Version + 1);
        db.Events.Add(new LedgerEvent
        {
            StreamId = account.Id, Version = account.Version, Type = "account-status-changed.v1", OccurredAt = now,
            Data = EnvelopeSerializer.ToPayload(new StatusEvent(status)),
            Metadata = EnvelopeSerializer.ToPayload(new { CorrelationId = account.Id, CausationId = (Guid?)null, OperationId = (Guid?)null })
        });
        outbox.Enqueue(new StatusMessage(account.Id, status, now, account.Version), account.Id);
    }
}
