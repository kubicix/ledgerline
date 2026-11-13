using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Accounts;

[MessageType("accounts.command.refund-funds.v1")]
public sealed record RefundFunds(Guid OperationId, Guid PaymentId, Guid RefundId, Guid MerchantAccountId,
    Guid CustomerAccountId, long AmountMinor, string Currency);
