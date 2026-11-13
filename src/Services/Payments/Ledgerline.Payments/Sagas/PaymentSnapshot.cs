namespace Ledgerline.Payments.Sagas;

public sealed record PaymentSnapshot(Guid Id, PaymentKind Kind, PaymentStatus Status, SagaStep Step,
    int StepAttempt, Guid CustomerId, Guid? SourceAccountId, Guid DestinationAccountId,
    long AmountMinor, string Currency, string? FailureReason = null);
