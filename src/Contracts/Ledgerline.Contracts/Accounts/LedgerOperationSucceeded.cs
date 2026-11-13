using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Accounts;

[MessageType("accounts.event.ledger-operation-succeeded.v1")]
public sealed record LedgerOperationSucceeded(Guid OperationId, Guid PaymentId, LedgerOperationKind Kind, Guid JournalEntryId, DateTimeOffset PostedAt);
