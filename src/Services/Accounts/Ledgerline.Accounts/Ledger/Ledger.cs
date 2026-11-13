using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.BuildingBlocks;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Accounts.Ledger;

public sealed class Ledger(AccountsDbContext db, EventStore events, IClock clock) : ILedger
{
    public async Task<PostingResult> PostAsync(PostingRequest request, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Posting requires an ambient Accounts transaction.");
        PostingValidator.EnsureWellFormed(request);
        var ids = request.Lines.Select(x => x.AccountId).ToArray();
        var accounts = await db.Accounts.FromSql($"SELECT * FROM accounts WHERE id = ANY({ids}) ORDER BY id FOR UPDATE").ToListAsync(ct);
        var reason = RejectionReason(request, accounts);
        if (reason is not null) return new PostingResult.Rejected(reason);
        var entry = new JournalEntry
        {
            Id = Ids.New(), EntryType = request.EntryType.ToString(), OperationId = request.OperationId,
            PaymentId = request.PaymentId, Currency = request.Currency, PostedAt = clock.UtcNow
        };
        var appended = AppendEvents(request, accounts.ToDictionary(x => x.Id), entry);
        db.JournalEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        db.JournalLines.AddRange(appended.Select((item, index) => new JournalLine
        {
            JournalEntryId = entry.Id, LineNo = index + 1, AccountId = item.Line.AccountId,
            Direction = item.Line.Direction.ToString(), AmountMinor = item.Line.AmountMinor,
            BalanceAfterMinor = item.Event.Data.GetProperty("balanceAfterMinor").GetInt64(), EventGlobalSeq = item.Event.GlobalSeq
        }));
        await db.SaveChangesAsync(ct);
        return new PostingResult.Posted(entry.Id, entry.PostedAt);
    }

    private static string? RejectionReason(PostingRequest request, List<Account> accounts)
    {
        if (accounts.Count != request.Lines.Count) return "AccountNotFound";
        if (accounts.Any(x => x.Currency != request.Currency)) return "CurrencyMismatch";
        var inactive = accounts.FirstOrDefault(x => x.Status != "Active" && !request.SkipStatusCheckFor.Contains(x.Id));
        if (inactive is not null) return inactive.Status == "Closed" ? "AccountClosed" : "AccountFrozen";
        var byId = accounts.ToDictionary(x => x.Id);
        return request.Lines.Any(line => line.Direction == Direction.Debit && byId[line.AccountId].Type != "System"
            && byId[line.AccountId].BalanceMinor < line.AmountMinor) ? "InsufficientFunds" : null;
    }

    private List<(PostingLine Line, LedgerEvent Event)> AppendEvents(PostingRequest request, Dictionary<Guid, Account> accounts, JournalEntry entry)
    {
        var appended = new List<(PostingLine, LedgerEvent)>();
        foreach (var line in request.Lines)
        {
            var account = accounts[line.AccountId];
            account.BalanceMinor = checked(account.BalanceMinor + (line.Direction == Direction.Credit ? line.AmountMinor : -line.AmountMinor));
            account.Version = checked(account.Version + 1);
            var payload = new LedgerEntryPosted(entry.Id, request.EntryType, request.PaymentId, line.Direction,
                line.AmountMinor, request.Currency, account.BalanceMinor);
            var stored = events.Append(account.Id, account.Version, payload,
                new LedgerEventMetadata(request.PaymentId, null, request.OperationId), entry.PostedAt);
            appended.Add((line, stored));
        }
        return appended;
    }
}
