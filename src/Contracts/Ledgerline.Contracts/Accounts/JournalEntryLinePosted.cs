using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Accounts;

/// <summary>
/// One line of a posted journal entry, as relayed to Kafka (one record per line of <see cref="JournalEntryPosted"/>).
/// <see cref="LineNo"/> is 1-based and follows the order of <see cref="JournalEntryPosted.Lines"/>.
/// </summary>
[MessageType("accounts.event.journal-entry-line-posted.v1")]
public sealed record JournalEntryLinePosted(Guid JournalEntryId, string EntryType, Guid PaymentId, string Currency,
    DateTimeOffset PostedAt, Guid AccountId, string Direction, long AmountMinor, long BalanceAfterMinor, int LineNo)
{
    /// <summary>The entry's lines as records, numbered from 1 in the order of <see cref="JournalEntryPosted.Lines"/>.</summary>
    public static IEnumerable<JournalEntryLinePosted> From(JournalEntryPosted entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Lines.Select((line, index) => new JournalEntryLinePosted(entry.JournalEntryId, entry.EntryType,
            entry.PaymentId, entry.Currency, entry.PostedAt, line.AccountId, line.Direction, line.AmountMinor,
            line.BalanceAfterMinor, index + 1));
    }
}
