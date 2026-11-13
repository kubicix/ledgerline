using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Accounts;

[MessageType("accounts.command.credit-top-up.v1")]
public sealed record CreditTopUp(Guid OperationId, Guid PaymentId, Guid AccountId, long AmountMinor, string Currency);
