using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Infrastructure.Entities;

public sealed class SagaTimeout
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public SagaStep Step { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public Guid? CommandMessageId { get; set; }
    public int DurationMs { get; set; }
    public Guid? RefundId { get; set; }
}
