using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Reconciliation;

/// <summary>A reconciliation run completed (reconciliation.md section 4). <see cref="Mismatched"/> counts the run's items.</summary>
[MessageType("reconciliation.event.reconciliation-completed.v1")]
public sealed record ReconciliationCompleted(Guid RunId, DateOnly BusinessDate, int Matched, int Mismatched, bool InputsIncomplete,
    DateTimeOffset CompletedAt);
