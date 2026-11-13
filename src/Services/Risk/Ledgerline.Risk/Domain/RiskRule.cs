namespace Ledgerline.Risk.Domain;

public sealed record RiskRule(Guid Id, RuleCode Code, string? Kind, string? Currency,
    long? ThresholdMinor, int? MaxCount, int? WindowSeconds, bool Enabled);
