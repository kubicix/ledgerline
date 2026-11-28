namespace Ledgerline.Reconciliation.Matching;

public sealed record ReconciliationResult(IReadOnlyList<Guid> MatchedPaymentIds, IReadOnlyList<Mismatch> Mismatches);
