namespace Ledgerline.Reconciliation.Infrastructure.Entities;

/// <summary>A row of <c>reconciliation_items</c>: one mismatch found by a run.</summary>
public sealed class ReconciliationItem
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string Code { get; set; } = "";
    public string Reference { get; set; } = "";
    public long? BankAmountMinor { get; set; }
    public long? LedgerAmountMinor { get; set; }
    public string? Currency { get; set; }
}
