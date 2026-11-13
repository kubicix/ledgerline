using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Risk;

[MessageType("risk.event.risk-approved.v1")]
public sealed record RiskApproved(Guid PaymentId, DateTimeOffset EvaluatedAt);
