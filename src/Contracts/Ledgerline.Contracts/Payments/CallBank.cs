using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Payments;

[MessageType("payments.command.call-bank.v1")]
public sealed record CallBank(Guid PaymentId, string Operation);
