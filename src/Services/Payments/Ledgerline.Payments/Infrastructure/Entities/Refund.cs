using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Infrastructure.Entities;

public sealed class Refund
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public long AmountMinor { get; set; }
    public RefundStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public Guid RequestedBy { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
