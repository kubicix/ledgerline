using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Infrastructure.Entities;

public sealed class SagaStepRow
{
    public long Id { get; set; }
    public Guid PaymentId { get; set; }
    public SagaStep Step { get; set; }
    public string Outcome { get; set; } = "";
    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; }
}
