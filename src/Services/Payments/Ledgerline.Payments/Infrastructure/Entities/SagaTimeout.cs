using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Infrastructure.Entities;

public sealed class SagaTimeout
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public SagaStep Step { get; set; }
    public DateTimeOffset DueAt { get; set; }
}
