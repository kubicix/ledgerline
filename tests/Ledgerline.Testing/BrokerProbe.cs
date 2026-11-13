using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Ledgerline.Testing;

public sealed class BrokerProbe : IAsyncDisposable
{
    private readonly IConnection connection;
    private readonly IChannel channel;
    private readonly ConcurrentQueue<MessageEnvelope> messages = new();
    private readonly SemaphoreSlim publishingGate = new(1, 1);
    public IReadOnlyList<MessageEnvelope> Messages => messages.ToArray();

    private BrokerProbe(IConnection connection, IChannel channel)
    {
        this.connection = connection;
        this.channel = channel;
    }

    public static async Task<BrokerProbe> CreateAsync(string rabbitUri, params string[] routingKeyPatterns)
    {
        var connection = await new ConnectionFactory { Uri = new Uri(rabbitUri) }.CreateConnectionAsync();
        try
        {
            var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
            var probe = new BrokerProbe(connection, channel);
            await probe.ListenAsync(routingKeyPatterns);
            return probe;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task ListenAsync(string[] patterns)
    {
        var queue = await channel.QueueDeclareAsync(exclusive: true, autoDelete: true);
        foreach (var pattern in patterns)
        {
            var exchange = MessageTypeRegistry.ExchangeFor(pattern);
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true);
            await channel.QueueBindAsync(queue.QueueName, exchange, pattern);
        }
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            messages.Enqueue(EnvelopeSerializer.Deserialize(delivery.Body.Span));
            return Task.CompletedTask;
        };
        await channel.BasicConsumeAsync(queue.QueueName, true, consumer);
    }

    public async Task<T> WaitForAsync<T>(Func<T, bool> predicate, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var typeName = new MessageTypeRegistry().NameOf<T>();
        T result = default!;
        await Eventually.ShouldAsync(() =>
        {
            foreach (var envelope in messages.Where(m => m.Type == typeName))
            {
                var payload = (T)EnvelopeSerializer.FromPayload(envelope.Payload, typeof(T));
                if (!predicate(payload)) continue;
                result = payload;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }, timeout, $"Expected message {typeName}");
        return result;
    }

    public Task PublishAsync<T>(T payload, Guid correlationId, Guid? messageId = null) =>
        PublishRawAsync(new MessageEnvelope(messageId ?? Ids.New(), new MessageTypeRegistry().NameOf<T>(),
            DateTimeOffset.UtcNow, correlationId, null, Activity.Current?.Id, EnvelopeSerializer.ToPayload(payload)));

    public async Task PublishRawAsync(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await publishingGate.WaitAsync();
        try
        {
            var exchange = MessageTypeRegistry.ExchangeFor(envelope.Type);
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true);
            var properties = new BasicProperties
            {
                MessageId = envelope.MessageId.ToString(), Type = envelope.Type,
                CorrelationId = envelope.CorrelationId.ToString(), ContentType = "application/json", Persistent = true,
                Headers = new Dictionary<string, object?> { ["x-ledgerline-attempt"] = 1 }
            };
            if (envelope.TraceParent is not null)
                properties.Headers["traceparent"] = Encoding.UTF8.GetBytes(envelope.TraceParent);
            await channel.BasicPublishAsync(exchange, envelope.Type, true, properties, EnvelopeSerializer.Serialize(envelope));
        }
        finally
        {
            publishingGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await channel.DisposeAsync();
        await connection.DisposeAsync();
        publishingGate.Dispose();
    }
}
