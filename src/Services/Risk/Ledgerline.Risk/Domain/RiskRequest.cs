namespace Ledgerline.Risk.Domain;

public sealed record RiskRequest(Guid PaymentId, string Kind, Guid CustomerId, Guid AccountId,
    long AmountMinor, string Currency, DateTimeOffset RequestedAt);
