using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Accounts;

[MessageType("accounts.command.release-funds.v1")]
public sealed record ReleaseFunds(Guid OperationId, Guid PaymentId);
