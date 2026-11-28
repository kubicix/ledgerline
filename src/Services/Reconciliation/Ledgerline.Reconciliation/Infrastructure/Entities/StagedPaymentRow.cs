namespace Ledgerline.Reconciliation.Infrastructure.Entities;

/// <summary>A row of <c>staged_payments</c>: the latest state of a payment by its event time.</summary>
public sealed class StagedPaymentRow
{
    public Guid PaymentId { get; set; }
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "";
    public string? BankCaptureId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The envelope message ID of the event the row's current state comes from.</summary>
    public Guid LastMessageId { get; set; }
}
