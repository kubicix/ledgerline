namespace Ledgerline.BankSimulator.Infrastructure.Entities;

public sealed class BankAuthorization
{
    public string Id { get; set; } = "";
    public string Reference { get; set; } = "";
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "";
    public string CardToken { get; set; } = "";
    public string Status { get; set; } = "Authorized";
    public string? CaptureId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
