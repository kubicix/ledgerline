using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Payments;

[MessageType("payments.event.payment-authorized.v1")]
public sealed record PaymentAuthorized(Guid PaymentId, string Kind, Guid CustomerId, long AmountMinor, string Currency,
    Guid? SourceAccountId, Guid DestinationAccountId, DateTimeOffset OccurredAt, DateTimeOffset AuthorizationExpiresAt);
