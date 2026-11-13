namespace Ledgerline.Risk.Domain;

public sealed record RiskOutcome(bool Approved, IReadOnlyList<string> RuleCodes);
