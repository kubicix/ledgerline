using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Accounts;

[MessageType("accounts.command.transfer-funds.v1")]
public sealed record TransferFunds(Guid OperationId, Guid PaymentId, Guid FromAccountId, Guid ToAccountId, long AmountMinor, string Currency);
