namespace Ledgerline.Reconciliation.Infrastructure.Entities;

/// <summary>A row of <c>staged_ledger_lines</c>: one line of a posted journal entry.</summary>
public sealed class StagedLedgerLineRow
{
    public Guid JournalEntryId { get; set; }
    public int LineNo { get; set; }
    public Guid AccountId { get; set; }
    public bool IsBankClearing { get; set; }
    public string EntryType { get; set; } = "";
    public string Direction { get; set; } = "";
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "";
    public Guid PaymentId { get; set; }
    public DateTimeOffset PostedAt { get; set; }
}
