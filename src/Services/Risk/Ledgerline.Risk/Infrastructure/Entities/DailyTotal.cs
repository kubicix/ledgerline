namespace Ledgerline.Risk.Infrastructure.Entities;

public sealed class DailyTotal
{
    public Guid CustomerId { get; set; }
    public DateOnly Day { get; set; }
    public string Currency { get; set; } = "";
    public long TotalMinor { get; set; }
}
