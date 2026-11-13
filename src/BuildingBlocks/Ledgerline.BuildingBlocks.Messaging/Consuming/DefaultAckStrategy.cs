using RabbitMQ.Client;

namespace Ledgerline.BuildingBlocks.Messaging.Consuming;

public sealed class DefaultAckStrategy : IAckStrategy
{
    public Task AckAsync(IChannel channel, ulong deliveryTag, CancellationToken ct) =>
        channel.BasicAckAsync(deliveryTag, false, ct).AsTask();
}
