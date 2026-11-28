using System.Reflection;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Reconciliation.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Reconciliation.Staging;

/// <summary>
/// Stages every journal line of <c>ledgerline.ledger.entries.v1</c> into <c>staged_ledger_lines</c>, once per
/// (entry, line). All entry types are staged: the trial balance sums every line. A line is flagged as bank clearing
/// when its account is the <c>BANK_CLEARING</c> account of the line's currency.
/// </summary>
public sealed class LedgerStagingConsumer : IStagingConsumer
{
    public IReadOnlyCollection<string> MessageTypes { get; } = [typeof(JournalEntryLinePosted).GetCustomAttribute<MessageTypeAttribute>()!.Name];

    public async Task ApplyAsync(MessageEnvelope envelope, ReconciliationDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(db);
        var line = (JournalEntryLinePosted)EnvelopeSerializer.FromPayload(envelope.Payload, typeof(JournalEntryLinePosted));
        if (line.JournalEntryId == Guid.Empty || line.LineNo < 1 || line.AccountId == Guid.Empty || line.PaymentId == Guid.Empty ||
            string.IsNullOrEmpty(line.EntryType) || string.IsNullOrEmpty(line.Direction) || string.IsNullOrEmpty(line.Currency) ||
            line.PostedAt == default)
            throw new InvalidDataException("The ledger line lacks its entry, line number, account, payment, type, direction, currency or time.");
        var isBankClearing = line.AccountId == BankClearingAccounts.IdFor(line.Currency);
        var postedAt = line.PostedAt.ToUniversalTime();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO staged_ledger_lines (journal_entry_id, line_no, account_id, is_bank_clearing, entry_type, direction,
                amount_minor, currency, payment_id, posted_at)
            VALUES ({line.JournalEntryId}, {line.LineNo}, {line.AccountId}, {isBankClearing}, {line.EntryType}, {line.Direction},
                {line.AmountMinor}, {line.Currency}, {line.PaymentId}, {postedAt})
            ON CONFLICT (journal_entry_id, line_no) DO NOTHING
            """, ct);
    }
}
