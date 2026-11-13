using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Infrastructure.Entities;

public sealed class Payment
{
    public Guid Id { get; set; }
    public PaymentKind Kind { get; set; }
    public PaymentStatus Status { get; set; }
    public SagaStep SagaStep { get; set; }
    public int StepAttempt { get; set; } = 1;
    public Guid CustomerId { get; set; }
    public Guid? SourceAccountId { get; set; }
    public Guid DestinationAccountId { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "";
    public CaptureMode? CaptureMode { get; set; }
    public long? CaptureRequestedMinor { get; set; }
    public long CapturedMinor { get; set; }
    public long RefundedMinor { get; set; }
    public string? CardToken { get; set; }
    public string? BankAuthorizationId { get; set; }
    public string? BankCaptureId { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset? AuthorizationExpiresAt { get; set; }
    public DateTimeOffset? ReconciledAt { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; set; }

    public PaymentSnapshot Snapshot() => new(Id, Kind, Status, SagaStep, StepAttempt, CustomerId,
        SourceAccountId, DestinationAccountId, AmountMinor, Currency, FailureReason,
        BankAuthorizationId, BankCaptureId, CreatedAt, CaptureMode, CaptureRequestedMinor, AuthorizationExpiresAt);
}
