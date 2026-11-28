using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Reconciliation;

/// <summary>
/// One item of a completed run (reconciliation.md section 4). <see cref="Code"/> is a mismatch code such as
/// <c>BankCaptureNotInLedger</c>; <see cref="Reference"/> is the payment reference, or the currency for <c>LedgerImbalance</c>.
/// </summary>
[MessageType("reconciliation.event.reconciliation-mismatch-detected.v1")]
public sealed record ReconciliationMismatchDetected(Guid RunId, DateOnly BusinessDate, string Code, string Reference,
    long? BankAmountMinor, long? LedgerAmountMinor, string? Currency);
