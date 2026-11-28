using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Reconciliation;

/// <summary>Payments a completed run matched on all three sides, in batches of at most 500 IDs (reconciliation.md section 4).</summary>
[MessageType("reconciliation.event.payments-reconciled.v1")]
public sealed record PaymentsReconciled(Guid RunId, DateOnly BusinessDate, IReadOnlyList<Guid> PaymentIds);
