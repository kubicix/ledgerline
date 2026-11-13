using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Payments;

[MessageType("payments.event.payment-refunded.v1")]
public sealed record PaymentRefunded(Guid PaymentId, string Kind, Guid CustomerId, long AmountMinor, string Currency,
    Guid? SourceAccountId, Guid DestinationAccountId, DateTimeOffset OccurredAt,
    Guid RefundId, long RefundAmountMinor, long RefundedTotalMinor);
