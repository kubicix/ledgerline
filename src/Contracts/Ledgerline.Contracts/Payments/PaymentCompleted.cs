using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Payments;

[MessageType("payments.event.payment-completed.v1")]
public sealed record PaymentCompleted(Guid PaymentId, string Kind, Guid CustomerId, long AmountMinor, string Currency,
    Guid? SourceAccountId, Guid DestinationAccountId, DateTimeOffset OccurredAt, string? BankCaptureId);
