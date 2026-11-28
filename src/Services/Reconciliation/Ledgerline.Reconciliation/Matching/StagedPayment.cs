namespace Ledgerline.Reconciliation.Matching;

/// <summary>A payment as staged from the payments topic (reconciliation.md section 6).</summary>
public sealed record StagedPayment(
    Guid PaymentId, string Kind, string Status, long AmountMinor, string Currency, string? BankCaptureId, DateTimeOffset OccurredAt);
