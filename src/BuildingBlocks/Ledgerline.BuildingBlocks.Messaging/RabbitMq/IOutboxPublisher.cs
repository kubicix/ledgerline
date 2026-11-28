using Ledgerline.BuildingBlocks.Messaging.Outbox;

namespace Ledgerline.BuildingBlocks.Messaging.RabbitMq;

public interface IOutboxPublisher
{
    /// <summary>The <c>transport</c> column value of the outbox rows this publisher sends; the relay runs one loop per publisher.</summary>
    string Transport { get; }
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}
