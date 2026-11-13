namespace Ledgerline.Payments.Sagas;

public sealed record PaymentSnapshot(Guid Id, PaymentKind Kind, PaymentStatus Status, SagaStep Step,
    int StepAttempt, Guid CustomerId, Guid? SourceAccountId, Guid DestinationAccountId,
    long AmountMinor, string Currency, string? FailureReason = null,
    string? BankAuthorizationId = null, string? BankCaptureId = null, DateTimeOffset? CreatedAt = null,
    CaptureMode? CaptureMode = null, long? CaptureRequestedMinor = null, DateTimeOffset? AuthorizationExpiresAt = null,
    Guid? CaptureOperationId = null, long CapturedMinor = 0, long RefundedMinor = 0);
