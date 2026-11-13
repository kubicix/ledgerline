namespace Ledgerline.Accounts.Infrastructure.Entities;

public sealed class Account
{
    public Guid Id { get; set; }
    public Guid? OwnerId { get; set; }
    public string Type { get; set; } = "";
    public string? SystemCode { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "";
    public long BalanceMinor { get; set; }
    public int Version { get; set; }
    public string? ContactEmail { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? RequestHash { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
}
