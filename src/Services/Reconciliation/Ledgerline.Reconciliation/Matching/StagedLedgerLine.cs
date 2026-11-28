namespace Ledgerline.Reconciliation.Matching;

/// <summary>A journal line as staged from the ledger topic. The matcher only reads <see cref="IsBankClearing"/>.</summary>
public sealed record StagedLedgerLine(
    Guid JournalEntryId, int LineNo, Guid AccountId, bool IsBankClearing, string EntryType, string Direction,
    long AmountMinor, string Currency, Guid PaymentId, DateTimeOffset PostedAt);
