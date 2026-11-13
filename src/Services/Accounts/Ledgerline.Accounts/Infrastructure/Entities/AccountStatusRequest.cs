namespace Ledgerline.Accounts.Infrastructure.Entities;

public sealed class AccountStatusRequest
{
    public Guid CallerId { get; set; }
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid AccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
