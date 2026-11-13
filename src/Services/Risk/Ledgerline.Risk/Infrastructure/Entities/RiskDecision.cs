namespace Ledgerline.Risk.Infrastructure.Entities;

public sealed class RiskDecision
{
    public Guid PaymentId { get; set; }
    public Guid CustomerId { get; set; }
    public string Kind { get; set; } = "";
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string[] RuleCodes { get; set; } = [];
    public DateTimeOffset EvaluatedAt { get; set; }
}
