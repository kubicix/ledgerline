using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Accounts;

[MessageType("accounts.command.capture-funds.v1")]
public sealed record CaptureFunds(Guid OperationId, Guid PaymentId, Guid MerchantAccountId, long CaptureAmountMinor);
