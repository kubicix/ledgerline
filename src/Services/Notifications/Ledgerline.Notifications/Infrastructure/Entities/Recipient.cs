namespace Ledgerline.Notifications.Infrastructure.Entities;

public sealed class Recipient
{
    public Guid AccountId { get; set; }
    public Guid OwnerId { get; set; }
    public string Email { get; set; } = "";
}
