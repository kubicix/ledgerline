using Ledgerline.BuildingBlocks.Messaging.Outbox;

namespace Ledgerline.BuildingBlocks.Messaging.RabbitMq;

public interface IOutboxPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}
