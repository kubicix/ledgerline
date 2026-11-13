namespace Ledgerline.Accounts.Infrastructure.Entities;

public sealed class Hold
{
    public Guid PaymentId { get; set; }
    public Guid AccountId { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "";
}
