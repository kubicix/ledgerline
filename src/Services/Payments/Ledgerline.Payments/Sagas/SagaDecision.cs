namespace Ledgerline.Payments.Sagas;

public sealed record SagaDecision(PaymentStatus Status, SagaStep Step, IReadOnlyList<SagaEffect> Effects,
    string? FailureReason, bool Ignored);
