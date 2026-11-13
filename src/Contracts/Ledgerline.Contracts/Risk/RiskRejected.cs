using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Risk;

[MessageType("risk.event.risk-rejected.v1")]
public sealed record RiskRejected(Guid PaymentId, IReadOnlyList<string> RuleCodes, DateTimeOffset EvaluatedAt);
