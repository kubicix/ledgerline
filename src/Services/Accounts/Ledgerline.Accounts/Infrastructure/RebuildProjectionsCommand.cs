using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Ledgerline.Accounts.Infrastructure;

public static class RebuildProjectionsCommand
{
    public static async Task RunAsync(string connectionString, CancellationToken ct = default)
    {
        await using var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(connectionString).Options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE accounts,journal_entries,journal_lines,holds IN ACCESS EXCLUSIVE MODE", ct);
        var history = await db.Events.AsNoTracking().OrderBy(x => x.GlobalSeq).ToListAsync(ct);
        var accounts = new Dictionary<Guid, Account>();
        var entries = new Dictionary<Guid, JournalEntry>();
        var lines = new List<JournalLine>();
        var holds = new Dictionary<Guid, Hold>();
        foreach (var item in history) Apply(item, accounts, entries, lines, holds);
        await db.Database.ExecuteSqlRawAsync("TRUNCATE accounts,journal_entries,journal_lines,holds", ct);
        db.Accounts.AddRange(accounts.Values);
        db.JournalEntries.AddRange(entries.Values);
        db.JournalLines.AddRange(lines);
        db.Holds.AddRange(holds.Values);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static void Apply(LedgerEvent item, Dictionary<Guid, Account> accounts,
        Dictionary<Guid, JournalEntry> entries, List<JournalLine> lines, Dictionary<Guid, Hold> holds)
    {
        if (item.Type == "account-opened.v1")
        {
            var opened = (AccountOpened)EnvelopeSerializer.FromPayload(item.Data, typeof(AccountOpened));
            accounts.Add(item.StreamId, new Account
            {
                Id = item.StreamId, OwnerId = opened.OwnerId, Type = opened.Type, SystemCode = opened.SystemCode,
                Currency = opened.Currency, Status = "Active", Version = item.Version, ContactEmail = opened.ContactEmail,
                OpenedAt = item.OccurredAt, IdempotencyKey = ReadNullable(item.Metadata, "idempotencyKey"), RequestHash = ReadNullable(item.Metadata, "requestHash")
            });
            return;
        }
        var account = accounts[item.StreamId];
        if (item.Version != account.Version + 1) throw new InvalidDataException("Ledger stream versions are not contiguous.");
        account.Version = item.Version;
        if (item.Type == "account-status-changed.v1")
            account.Status = ((AccountStatusChanged)EnvelopeSerializer.FromPayload(item.Data, typeof(AccountStatusChanged))).Status;
        else if (item.Type == "ledger-entry-posted.v1") ApplyEntry(item, account, entries, lines, holds);
        else throw new InvalidDataException("Unknown ledger event type.");
    }

    private static void ApplyEntry(LedgerEvent item, Account account, Dictionary<Guid, JournalEntry> entries, List<JournalLine> lines, Dictionary<Guid, Hold> holds)
    {
        var posted = (LedgerEntryPosted)EnvelopeSerializer.FromPayload(item.Data, typeof(LedgerEntryPosted));
        ApplyHold(account.Id, posted, holds);
        account.BalanceMinor = checked(account.BalanceMinor + (posted.Direction == Direction.Credit ? posted.AmountMinor : -posted.AmountMinor));
        if (account.BalanceMinor != posted.BalanceAfterMinor) throw new InvalidDataException("Event balance does not match replay.");
        entries.TryAdd(posted.JournalEntryId, new JournalEntry
        {
            Id = posted.JournalEntryId, EntryType = posted.EntryType.ToString(), PaymentId = posted.PaymentId,
            OperationId = item.Metadata.GetProperty("operationId").GetGuid(), Currency = posted.Currency, PostedAt = item.OccurredAt
        });
        lines.Add(new JournalLine
        {
            JournalEntryId = posted.JournalEntryId, LineNo = lines.Count(x => x.JournalEntryId == posted.JournalEntryId) + 1,
            AccountId = account.Id, Direction = posted.Direction.ToString(), AmountMinor = posted.AmountMinor,
            BalanceAfterMinor = account.BalanceMinor, EventGlobalSeq = item.GlobalSeq
        });
    }

    private static void ApplyHold(Guid accountId, LedgerEntryPosted posted, Dictionary<Guid, Hold> holds)
    {
        if (posted.Direction != Direction.Debit) return;
        if (posted.EntryType == EntryType.Hold)
            holds.Add(posted.PaymentId, new Hold { PaymentId = posted.PaymentId, AccountId = accountId,
                AmountMinor = posted.AmountMinor, Currency = posted.Currency, Status = "Active" });
        else if (posted.EntryType is EntryType.Capture or EntryType.Release)
            holds[posted.PaymentId].Status = posted.EntryType == EntryType.Capture ? "Captured" : "Released";
    }

    private static string? ReadNullable(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
}
