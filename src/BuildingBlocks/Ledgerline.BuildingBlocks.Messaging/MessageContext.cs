namespace Ledgerline.BuildingBlocks.Messaging;

public sealed record MessageContext(Guid MessageId, Guid CorrelationId, Guid? CausationId, int Attempt);
