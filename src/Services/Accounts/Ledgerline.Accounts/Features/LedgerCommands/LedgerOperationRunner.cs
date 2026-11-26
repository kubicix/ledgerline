using System.Diagnostics;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Accounts.Features.LedgerCommands;

public sealed class LedgerOperationRunner(AccountsDbContext db, ILedger ledger, IOutbox outbox, IClock clock, ICrashPoint crashPoint,
    LedgerMetrics metrics) : IPostCommitHook
{
    private bool handled;

    public Task RunAsync(Guid operationId, Guid paymentId, LedgerOperationKind kind, Func<PostingRequest> build,
        MessageContext context, CancellationToken ct) =>
        RunAsync(operationId, paymentId, kind, () => ledger.PostAsync(build(), ct), context, ct);

    public async Task RunAsync(Guid operationId, Guid paymentId, LedgerOperationKind kind, Func<Task<PostingResult>> execute,
        MessageContext context, CancellationToken ct)
    {
        var existing = await db.LedgerOperations.FindAsync([operationId], ct);
        if (existing is not null)
        {
            await ReplyAsync(existing, context, ct);
            handled = true;
            return;
        }
        var started = Stopwatch.GetTimestamp();
        var outcome = "Failed";
        PostingResult result;
        try
        {
            result = await execute();
            outcome = result is PostingResult.Posted ? "Succeeded" : "Rejected";
        }
        // Recorded also when the posting throws (deadlock, serialization failure, timeout): that tail is what the metric is for.
        finally { metrics.RecordPosting(Stopwatch.GetElapsedTime(started), kind.ToString(), outcome); }
        var operation = new LedgerOperation
        {
            OperationId = operationId, PaymentId = paymentId, Kind = kind.ToString(), CompletedAt = clock.UtcNow,
            Outcome = result is PostingResult.Posted ? "Succeeded" : "Rejected",
            ReasonCode = (result as PostingResult.Rejected)?.ReasonCode,
            JournalEntryId = (result as PostingResult.Posted)?.JournalEntryId
        };
        db.LedgerOperations.Add(operation);
        var entry = await ReplyAsync(operation, context, ct);
        if (entry is not null) await PublishJournalAsync(entry, context, ct);
        handled = true;
    }

    private async Task<JournalEntry?> ReplyAsync(LedgerOperation operation, MessageContext context, CancellationToken ct)
    {
        var kind = Enum.Parse<LedgerOperationKind>(operation.Kind);
        if (operation.Outcome == "Rejected")
        {
            outbox.Enqueue(new LedgerOperationRejected(operation.OperationId, operation.PaymentId, kind, operation.ReasonCode!), context.CorrelationId, context.MessageId);
            return null;
        }
        var entry = await db.JournalEntries.AsNoTracking().SingleAsync(x => x.Id == operation.JournalEntryId, ct);
        outbox.Enqueue(new LedgerOperationSucceeded(operation.OperationId, operation.PaymentId, kind, entry.Id, entry.PostedAt), context.CorrelationId, context.MessageId);
        return entry;
    }

    private async Task PublishJournalAsync(JournalEntry entry, MessageContext context, CancellationToken ct)
    {
        var lines = await db.JournalLines.AsNoTracking().Where(x => x.JournalEntryId == entry.Id).OrderBy(x => x.LineNo)
            .Select(x => new JournalEntryPostedLine(x.AccountId, x.Direction, x.AmountMinor, x.BalanceAfterMinor)).ToListAsync(ct);
        outbox.Enqueue(new JournalEntryPosted(entry.Id, entry.EntryType, entry.PaymentId, entry.Currency, entry.PostedAt, lines), context.CorrelationId, context.MessageId);
    }

    public void OnCommitted()
    {
        if (handled) crashPoint.Hit("ledger.after-commit");
    }
}
