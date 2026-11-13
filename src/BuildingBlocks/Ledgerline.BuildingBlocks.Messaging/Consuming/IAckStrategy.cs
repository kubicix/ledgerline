using RabbitMQ.Client;

namespace Ledgerline.BuildingBlocks.Messaging.Consuming;

public interface IAckStrategy
{
    Task AckAsync(IChannel channel, ulong deliveryTag, CancellationToken ct);
}
