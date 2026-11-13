using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Accounts;

[MessageType("accounts.event.ledger-operation-rejected.v1")]
public sealed record LedgerOperationRejected(Guid OperationId, Guid PaymentId, LedgerOperationKind Kind, string ReasonCode);
