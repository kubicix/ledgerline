using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Accounts;

[MessageType("accounts.command.reserve-funds.v1")]
public sealed record ReserveFunds(Guid OperationId, Guid PaymentId, Guid AccountId, long AmountMinor, string Currency);
