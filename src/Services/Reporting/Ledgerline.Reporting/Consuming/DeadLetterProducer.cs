using System.Globalization;
using System.Text;
using Confluent.Kafka;

namespace Ledgerline.Reporting.Consuming;

/// <summary>
/// Writes a record the projections rejected to the dead topic (reporting.md section 7): the original key and value
/// unchanged, plus headers naming the error, the source position and the attempt count. Idempotent with
/// <c>acks = all</c>; a send completes only when Kafka reports the record persisted.
/// </summary>
public sealed partial class DeadLetterProducer : IDisposable
{
    public const string ErrorHeader = "error";
    public const string SourceTopicHeader = "source-topic";
    public const string SourcePartitionHeader = "source-partition";
    public const string SourceOffsetHeader = "source-offset";
    public const string AttemptsHeader = "attempts";
    private const int MessageTimeoutMs = 10_000;
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);
    private readonly IProducer<byte[], byte[]> producer;
    private readonly string topic;

    public DeadLetterProducer(ConsumerOptions options, ILogger<DeadLetterProducer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        topic = options.DeadLetterTopic;
        producer = new ProducerBuilder<byte[], byte[]>(new ProducerConfig
            {
                BootstrapServers = options.BootstrapServers,
                EnableIdempotence = true,
                Acks = Acks.All,
                MessageTimeoutMs = MessageTimeoutMs,
                RequestTimeoutMs = MessageTimeoutMs,
                LingerMs = 0
            })
            .SetLogHandler((_, log) => ClientLog(logger, log.Facility, log.Message))
            .SetErrorHandler((_, error) => ClientError(logger, error.Code, error.Reason))
            .Build();
    }

    /// <summary>
    /// The error header carries the exception type and message only, never the payload: an <c>account-opened</c>
    /// payload holds an email address.
    /// </summary>
    public async Task SendAsync(ConsumeResult<byte[], byte[]> record, Exception error, int attempts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(error);
        var headers = new Headers
        {
            { ErrorHeader, Encoding.UTF8.GetBytes(error.GetType().FullName + ": " + error.Message) },
            { SourceTopicHeader, Encoding.UTF8.GetBytes(record.Topic) },
            { SourcePartitionHeader, Encoding.UTF8.GetBytes(record.Partition.Value.ToString(CultureInfo.InvariantCulture)) },
            { SourceOffsetHeader, Encoding.UTF8.GetBytes(record.Offset.Value.ToString(CultureInfo.InvariantCulture)) },
            { AttemptsHeader, Encoding.UTF8.GetBytes(attempts.ToString(CultureInfo.InvariantCulture)) }
        };
        var delivery = await producer.ProduceAsync(topic,
            new Message<byte[], byte[]> { Key = record.Message.Key, Value = record.Message.Value, Headers = headers }, ct);
        if (delivery.Status != PersistenceStatus.Persisted)
            throw new KafkaException(new Error(ErrorCode.Local_Fail, "Kafka did not report the dead-letter record as persisted."));
    }

    public void Dispose()
    {
        try { producer.Flush(FlushTimeout); }
        catch (KafkaException) { }
        producer.Dispose();
    }

    [LoggerMessage(1, LogLevel.Debug, "Kafka dead-letter producer {Facility}: {Message}")]
    private static partial void ClientLog(ILogger logger, string facility, string message);
    [LoggerMessage(2, LogLevel.Debug, "Kafka dead-letter producer error {Code}: {Reason}")]
    private static partial void ClientError(ILogger logger, ErrorCode code, string reason);
}
