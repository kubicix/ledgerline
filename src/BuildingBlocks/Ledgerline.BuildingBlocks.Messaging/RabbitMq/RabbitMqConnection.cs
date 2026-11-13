using RabbitMQ.Client;

namespace Ledgerline.BuildingBlocks.Messaging.RabbitMq;

public sealed class RabbitMqConnection(RabbitMqOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;

    public async Task<IChannel> CreateChannelAsync(bool publisherConfirms, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            connection ??= await new ConnectionFactory
            {
                Uri = new Uri(options.Uri),
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true
            }.CreateConnectionAsync(ct);
            return await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirms, publisherConfirms), ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
            await connection.DisposeAsync();
        gate.Dispose();
    }
}
