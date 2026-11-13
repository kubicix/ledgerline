using System.Text;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using RabbitMQ.Client;

namespace Ledgerline.BuildingBlocks.Messaging.RabbitMq;

public sealed class RabbitMqOutboxPublisher(RabbitMqConnection connection) : IOutboxPublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var channel = await connection.CreateChannelAsync(true, timeout.Token);
            var envelope = new MessageEnvelope(message.MessageId, message.Type, message.OccurredAt,
                message.CorrelationId, message.CausationId, message.TraceParent, message.Payload);
            var properties = new BasicProperties
            {
                MessageId = message.MessageId.ToString(), Type = message.Type,
                CorrelationId = message.CorrelationId.ToString(), ContentType = "application/json", Persistent = true,
                Headers = new Dictionary<string, object?> { ["x-ledgerline-attempt"] = 1 }
            };
            if (message.TraceParent is not null)
                properties.Headers["traceparent"] = Encoding.UTF8.GetBytes(message.TraceParent);
            await channel.BasicPublishAsync(MessageTypeRegistry.ExchangeFor(message.Type), message.Type,
                true, properties, EnvelopeSerializer.Serialize(envelope), timeout.Token);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            throw new PublishFailedException("Message was not confirmed or could not be routed.", error);
        }
    }
}

public sealed class PublishFailedException : Exception
{
    public PublishFailedException() { }
    public PublishFailedException(string message) : base(message) { }
    public PublishFailedException(string message, Exception innerException) : base(message, innerException) { }
}
