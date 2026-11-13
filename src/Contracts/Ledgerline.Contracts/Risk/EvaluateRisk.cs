using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Risk;

[MessageType("risk.command.evaluate-risk.v1")]
public sealed record EvaluateRisk(Guid PaymentId, string Kind, Guid CustomerId, Guid AccountId,
    long AmountMinor, string Currency, DateTimeOffset RequestedAt);
