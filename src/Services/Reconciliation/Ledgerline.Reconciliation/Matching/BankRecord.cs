namespace Ledgerline.Reconciliation.Matching;

public enum BankRecordType
{
    Capture,
    Refund,
}

/// <summary>One line of the bank's settlement file. The reference stays a string: an unknown row need not be a GUID.</summary>
public sealed record BankRecord(
    BankRecordType Type, string BankId, string Reference, long AmountMinor, string Currency, DateTimeOffset OccurredAt);
