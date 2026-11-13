using Ledgerline.Risk.Domain;

namespace Ledgerline.Risk.Infrastructure.Entities;

public sealed class RiskRuleEntity
{
    public Guid Id { get; set; }
    public RuleCode Code { get; set; }
    public string? Kind { get; set; }
    public string? Currency { get; set; }
    public long? ThresholdMinor { get; set; }
    public int? MaxCount { get; set; }
    public int? WindowSeconds { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }

    public RiskRule ToRule() => new(Id, Code, Kind, Currency, ThresholdMinor, MaxCount, WindowSeconds, Enabled);
}
