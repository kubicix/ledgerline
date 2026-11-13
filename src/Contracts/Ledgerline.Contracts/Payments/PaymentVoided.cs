using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Payments;

[MessageType("payments.event.payment-voided.v1")]
public sealed record PaymentVoided(Guid PaymentId, string Kind, Guid CustomerId, long AmountMinor, string Currency,
    Guid? SourceAccountId, Guid DestinationAccountId, DateTimeOffset OccurredAt, string Reason);
