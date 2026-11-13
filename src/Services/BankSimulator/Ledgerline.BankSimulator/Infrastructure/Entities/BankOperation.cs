using System.Text.Json;

namespace Ledgerline.BankSimulator.Infrastructure.Entities;

public sealed class BankOperation
{
    public string IdempotencyKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? AuthorizationId { get; set; }
    public string? CaptureId { get; set; }
    public string? RefundId { get; set; }
    public string? Reference { get; set; }
    public long? AmountMinor { get; set; }
    public string? Currency { get; set; }
    public int HttpStatus { get; set; }
    public JsonElement ResponseBody { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
