namespace Ledgerline.BuildingBlocks.Messaging;

public interface IOutbox
{
    void Enqueue<T>(T message, Guid correlationId, Guid? causationId = null);
}

