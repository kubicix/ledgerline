namespace Ledgerline.Payments.Infrastructure.Entities;

public sealed class AccountRef
{
    public Guid AccountId { get; set; }
    public Guid OwnerId { get; set; }
    public string Type { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "";
    public int StatusVersion { get; set; }
}
