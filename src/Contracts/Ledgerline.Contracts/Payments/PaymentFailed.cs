using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Payments;

[MessageType("payments.event.payment-failed.v1")]
public sealed record PaymentFailed(Guid PaymentId, string Kind, Guid CustomerId, long AmountMinor, string Currency,
    Guid? SourceAccountId, Guid DestinationAccountId, DateTimeOffset OccurredAt, string Status, string ReasonCode);
