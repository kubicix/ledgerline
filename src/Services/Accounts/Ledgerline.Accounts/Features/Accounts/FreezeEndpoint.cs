using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.EntityFrameworkCore;
using StatusMessage = Ledgerline.Contracts.Accounts.AccountStatusChanged;
using StatusEvent = Ledgerline.Accounts.Ledger.AccountStatusChanged;

namespace Ledgerline.Accounts.Features.Accounts;

internal static class FreezeEndpoint
{
    internal static Task<IResult> HandleAsync(Guid id, AccountsDbContext db, IOutbox outbox, IClock clock, CancellationToken ct) =>
        ChangeAsync(id, "Frozen", db, outbox, clock, ct);

    internal static async Task<IResult> ChangeAsync(Guid id, string status, AccountsDbContext db, IOutbox outbox, IClock clock, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var account = await db.Accounts.FromSqlInterpolated($"SELECT * FROM accounts WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (account is null) return ProblemResults.NotFound();
        if (account.Type == "System") return ProblemResults.InvalidState("System accounts cannot be frozen or unfrozen.");
        if (account.Status == status) return Results.NoContent();
        var now = clock.UtcNow;
        account.Status = status;
        account.Version = checked(account.Version + 1);
        db.Events.Add(new LedgerEvent
        {
            StreamId = id, Version = account.Version, Type = "account-status-changed.v1", OccurredAt = now,
            Data = EnvelopeSerializer.ToPayload(new StatusEvent(status)),
            Metadata = EnvelopeSerializer.ToPayload(new { CorrelationId = id, CausationId = (Guid?)null, OperationId = (Guid?)null })
        });
        outbox.Enqueue(new StatusMessage(id, status, now), id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }
}
