using Ledgerline.BuildingBlocks.Messaging.Consuming;
using RabbitMQ.Client;

namespace Ledgerline.Testing.Faults;

public sealed class DroppingAckStrategy : IAckStrategy
{
    private int dropped;
    public Task Closed { get; private set; } = Task.CompletedTask;

    public async Task AckAsync(IChannel channel, ulong deliveryTag, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref dropped, 1) == 0)
        {
            Closed = Task.Run(async () => await channel.DisposeAsync(), CancellationToken.None);
            return;
        }
        await channel.BasicAckAsync(deliveryTag, false, ct);
    }
}
