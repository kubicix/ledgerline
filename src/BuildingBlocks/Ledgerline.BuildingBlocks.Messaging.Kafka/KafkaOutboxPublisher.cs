using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Ledgerline.BuildingBlocks.Messaging.Diagnostics;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Microsoft.Extensions.Logging;

namespace Ledgerline.BuildingBlocks.Messaging.Kafka;

/// <summary>
/// Produces the outbox rows of transport <c>kafka</c>, one record per row: topic and key from the
/// <see cref="KafkaTopicMap"/>, value the same envelope bytes RabbitMQ gets. A row counts as published only when the
/// delivery report says persisted, and a produce is bounded by <see cref="KafkaOptions.MessageTimeoutMs"/>.
/// </summary>
public sealed partial class KafkaOutboxPublisher : IOutboxPublisher, IDisposable
{
    public const string TransportName = "kafka";
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);
    private readonly KafkaOptions options;
    private readonly KafkaTopicMap topics;
    private readonly ILogger<KafkaOutboxPublisher> logger;
    private readonly Lock gate = new();
    private IProducer<string, byte[]> producer;

    public KafkaOutboxPublisher(KafkaOptions options, KafkaTopicMap topics, ILogger<KafkaOutboxPublisher> logger)
    {
        this.options = options;
        this.topics = topics;
        this.logger = logger;
        producer = Build();
    }

    public string Transport => TransportName;

    /// <summary>
    /// Idempotent with <c>acks = all</c> (reporting.md section 2). The relay waits for each delivery before the next
    /// produce, so linger would only add latency inside the relay transaction.
    /// </summary>
    public static ProducerConfig CreateProducerConfig(KafkaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = options.MessageTimeoutMs,
            RequestTimeoutMs = options.MessageTimeoutMs,
            LingerMs = 0
        };
    }

    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var (topic, record) = CreateRecord(message);
        var current = Volatile.Read(ref producer);
        try
        {
            // Only the host token: cancelling a produce does not withdraw the record, so it is never cut short here.
            var delivery = await current.ProduceAsync(topic, record, ct);
            if (delivery.Status != PersistenceStatus.Persisted)
                throw new PublishFailedException("Kafka did not report the record as persisted.");
        }
        catch (Exception error) when (error is not PublishFailedException && !ct.IsCancellationRequested)
        {
            if (error is KafkaException { Error.IsFatal: true }) Replace(current);
            throw new PublishFailedException("Record was not acknowledged by Kafka.", error);
        }
    }

    public void Dispose()
    {
        var current = Volatile.Read(ref producer);
        // Every row is awaited before it is marked, so nothing here is owed to the outbox; the flush is a courtesy.
        try { current.Flush(FlushTimeout); }
        catch (KafkaException) { }
        current.Dispose();
    }

    private (string Topic, Message<string, byte[]> Record) CreateRecord(OutboxMessage message)
    {
        if (!topics.TryGet(message.Type, out var topic))
            throw new PublishFailedException("No Kafka topic is configured for the message type.");
        string key;
        try { key = topic.Key(EnvelopeSerializer.FromPayload(message.Payload, topic.RecordType)); }
        catch (Exception error) { throw new PublishFailedException("The Kafka key could not be computed from the payload.", error); }
        if (Activity.Current is { Kind: ActivityKind.Producer } span && span.Source.Name == MessagingActivitySource.Name)
            span.SetTag("messaging.destination.name", topic.Topic).DisplayName = "publish " + topic.Topic;
        // Send the PRODUCER span's context, as the RabbitMQ publisher does (ADR 0009); the row keeps the enqueue context.
        var traceParent = MessagingActivitySource.OutgoingTraceParent(message.TraceParent);
        var envelope = new MessageEnvelope(message.MessageId, message.Type, message.OccurredAt,
            message.CorrelationId, message.CausationId, traceParent, message.Payload);
        var headers = new Headers();
        if (traceParent is not null) headers.Add("traceparent", Encoding.UTF8.GetBytes(traceParent));
        return (topic.Topic, new Message<string, byte[]> { Key = key, Value = EnvelopeSerializer.Serialize(envelope), Headers = headers });
    }

    /// <summary>A fatal error leaves the idempotent producer unusable; the next produce gets a fresh one.</summary>
    private void Replace(IProducer<string, byte[]> failed)
    {
        lock (gate)
        {
            if (!ReferenceEquals(producer, failed)) return;
            Volatile.Write(ref producer, Build());
        }
        ProducerReplaced(logger);
        failed.Dispose();
    }

    private IProducer<string, byte[]> Build() =>
        new ProducerBuilder<string, byte[]>(CreateProducerConfig(options))
            .SetLogHandler((_, log) => ClientLog(logger, log.Facility, log.Message))
            .SetErrorHandler((_, error) =>
            {
                if (error.IsFatal) FatalError(logger, error.Code, error.Reason);
                else ClientError(logger, error.Code, error.Reason);
            })
            .Build();

    [LoggerMessage(1, LogLevel.Debug, "Kafka client {Facility}: {Message}")]
    private static partial void ClientLog(ILogger logger, string facility, string message);
    [LoggerMessage(2, LogLevel.Debug, "Kafka client error {Code}: {Reason}")]
    private static partial void ClientError(ILogger logger, ErrorCode code, string reason);
    [LoggerMessage(3, LogLevel.Error, "Kafka producer failed fatally with {Code}: {Reason}")]
    private static partial void FatalError(ILogger logger, ErrorCode code, string reason);
    [LoggerMessage(4, LogLevel.Warning, "Kafka producer replaced after a fatal error.")]
    private static partial void ProducerReplaced(ILogger logger);
}
