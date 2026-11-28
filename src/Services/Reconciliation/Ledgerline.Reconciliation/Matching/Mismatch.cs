namespace Ledgerline.Reconciliation.Matching;

/// <summary>One reconciliation item. For <see cref="MismatchCode.LedgerImbalance"/> the reference is the currency code.</summary>
public sealed record Mismatch(MismatchCode Code, string Reference, long? BankAmountMinor, long? LedgerAmountMinor, string? Currency);
