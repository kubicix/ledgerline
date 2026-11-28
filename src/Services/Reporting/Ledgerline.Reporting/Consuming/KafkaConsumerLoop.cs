using Confluent.Kafka;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Reporting.Infrastructure;
using MongoDB.Driver;

namespace Ledgerline.Reporting.Consuming;

/// <summary>
/// Consumes the event topics into the projections (reporting.md sections 3 and 7). Offsets are committed by hand,
/// and only for records whose projection writes MongoDB acknowledged:
/// <list type="bullet">
/// <item>A batch is applied in partition order. When MongoDB fails, the applied prefix is committed, every partition
/// of the batch is sought back to its first unapplied record, and the loop backs off and consumes again. Such
/// failures are retried without limit and never dead-letter.</item>
/// <item>Any other failure of a record is retried in place up to <see cref="MaxAttempts"/> times; then the record goes
/// to the dead topic and, once Kafka persisted it there, counts as applied. The batch ends at that record: its
/// offset is committed at once and the rest of the batch is consumed again, so one batch retries at most one poison
/// record. Between two <c>Consume</c> calls the loop therefore spends at most the batch's successful writes, one
/// failed write (bounded by MongoDB's server selection timeout), and either the backoff or one record's four retry
/// waits plus the dead-letter produce: well under librdkafka's 300 s <c>max.poll.interval.ms</c> at the defaults.</item>
/// <item>A record whose type no projection handles counts as applied.</item>
/// <item>A failed offset commit replaces the consumer, which resumes from the offsets Kafka really holds.</item>
/// <item>Every batch taken and every applied prefix is reported to <see cref="LagTracker"/>.</item>
/// </list>
/// The blocking consumer runs on its own thread.
/// </summary>
public sealed partial class KafkaConsumerLoop : BackgroundService
{
    public const int MaxAttempts = 5;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);
    private readonly ConsumerOptions options;
    private readonly MongoCollections collections;
    private readonly DeadLetterProducer deadLetters;
    private readonly LagTracker lag;
    private readonly ILogger<KafkaConsumerLoop> logger;
    private readonly ILookup<string, IProjection> projections;

    public KafkaConsumerLoop(ConsumerOptions options, MongoCollections collections, IEnumerable<IProjection> projections,
        DeadLetterProducer deadLetters, LagTracker lag, ILogger<KafkaConsumerLoop> logger)
    {
        this.options = options;
        this.collections = collections;
        this.deadLetters = deadLetters;
        this.lag = lag;
        this.logger = logger;
        this.projections = projections
            .SelectMany(projection => projection.MessageTypes.Select(type => (Type: type, Projection: projection)))
            .ToLookup(x => x.Type, x => x.Projection, StringComparer.Ordinal);
    }

    /// <summary>
    /// MongoDB being away, not bad data: connection failures, server selection and operation timeouts, write concern
    /// failures, a primary stepping down or recovering, a full or paused connection pool, and errors the server
    /// labels retryable. Such records are retried without limit and never dead-lettered. Every other exception,
    /// other driver errors included (a duplicate key, a failed command), counts as an attempt of the record.
    /// </summary>
    /// <remarks>
    /// A single write (<c>ReplaceOne</c>, <c>UpdateOne</c>) runs as a bulk write, and the driver reports a write concern
    /// failure there as a <see cref="MongoWriteException"/> with <c>WriteConcernError</c> set and no <c>WriteError</c>,
    /// not as <see cref="MongoWriteConcernException"/>; a wtimeout carries no retryable label. A bulk write reports
    /// it the same way on <see cref="MongoBulkWriteException"/>.
    /// </remarks>
    public static bool IsTransient(Exception error) => error is TimeoutException or MongoConnectionException
        or MongoExecutionTimeoutException or MongoWriteConcernException or MongoNotPrimaryException
        or MongoWriteException { WriteError: null, WriteConcernError: not null }
        or MongoBulkWriteException { WriteErrors.Count: 0, WriteConcernError: not null }
        or MongoNodeIsRecoveringException or MongoWaitQueueFullException or MongoConnectionPoolPausedException
        || (error is MongoException mongo && (mongo.HasErrorLabel("RetryableWriteError") || mongo.HasErrorLabel("TransientTransactionError")));

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        try
        {
            EnsureIndexes(ct);
            while (!ct.IsCancellationRequested)
            {
                var consumer = Build();
                try
                {
                    consumer.Subscribe(options.EventTopics);
                    Poll(consumer, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception error)
                {
                    // A fatal client error, a failed commit or a failed seek: a new consumer resumes from the
                    // committed offsets. Records applied since then are applied again (idempotently).
                    ConsumerRestarting(logger, error);
                    Close(consumer);
                    Wait(options.RetryMaxDelay, ct);
                    continue;
                }
                Close(consumer);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void EnsureIndexes(CancellationToken ct)
    {
        for (var failures = 1; ; failures++)
        {
            try
            {
                collections.EnsureIndexesAsync(ct).GetAwaiter().GetResult();
                return;
            }
            catch (Exception error) when (IsTransient(error) && !ct.IsCancellationRequested)
            {
                IndexesFailed(logger, error);
                Wait(options.Backoff(failures), ct);
            }
        }
    }

    private void Poll(IConsumer<byte[], byte[]> consumer, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = NextBatch(consumer, ct);
            if (batch.Count == 0) continue;
            lag.Taken(consumer, batch);
            var (done, retry) = Apply(batch, ct);
            Commit(consumer, batch, done);
            if (done < batch.Count) Rewind(consumer, batch, done);
            lag.Applied(consumer, batch, done);
            if (!retry)
            {
                failures = 0;
                continue;
            }
            Wait(options.Backoff(++failures), ct);
        }
    }

    private List<ConsumeResult<byte[], byte[]>> NextBatch(IConsumer<byte[], byte[]> consumer, CancellationToken ct)
    {
        var batch = new List<ConsumeResult<byte[], byte[]>>();
        try
        {
            batch.Add(consumer.Consume(ct));
            while (batch.Count < options.MaxBatchSize && consumer.Consume(TimeSpan.Zero) is { } next) batch.Add(next);
        }
        catch (ConsumeException error) when (!error.Error.IsFatal)
        {
            // For example a subscribed topic that does not exist yet. The records taken so far are still applied.
            ConsumeFailed(logger, error.Error.Code, error.Error.Reason);
            if (batch.Count == 0) Wait(options.RetryMaxDelay, ct);
        }
        return batch;
    }

    /// <summary>
    /// Applies the batch in order. Returns how many records, from the start, are done, and whether the loop must back
    /// off before consuming the rest again. The batch also ends, without backoff, right after a dead-lettered record.
    /// </summary>
    private (int Done, bool Retry) Apply(List<ConsumeResult<byte[], byte[]>> batch, CancellationToken ct)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            switch (TryApply(batch[i], ct))
            {
                case Outcome.Retry: return (i, true);
                case Outcome.DeadLettered: return (i + 1, false);
            }
        }
        return (batch.Count, false);
    }

    /// <summary><see cref="Outcome.Retry"/> when the record must be consumed again: MongoDB or the dead topic is unavailable.</summary>
    private Outcome TryApply(ConsumeResult<byte[], byte[]> record, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ApplyOnce(record, ct);
                return Outcome.Applied;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (IsTransient(error))
            {
                StoreUnavailable(logger, record.Topic, record.Partition.Value, record.Offset.Value, error);
                return Outcome.Retry;
            }
            catch (Exception error)
            {
                AttemptFailed(logger, record.Topic, record.Partition.Value, record.Offset.Value, attempt, error);
                if (attempt < MaxAttempts)
                {
                    Wait(options.Backoff(attempt), ct);
                    continue;
                }
                return TryDeadLetter(record, error, attempt, ct);
            }
        }
    }

    private void ApplyOnce(ConsumeResult<byte[], byte[]> record, CancellationToken ct)
    {
        var envelope = EnvelopeSerializer.Deserialize(record.Message.Value);
        if (string.IsNullOrEmpty(envelope.Type)) throw new InvalidDataException("The envelope has no type.");
        if (!projections.Contains(envelope.Type))
        {
            Skipped(logger, envelope.Type, record.Topic, record.Partition.Value, record.Offset.Value);
            return;
        }
        foreach (var projection in projections[envelope.Type]) projection.ApplyAsync(envelope, ct).GetAwaiter().GetResult();
    }

    private Outcome TryDeadLetter(ConsumeResult<byte[], byte[]> record, Exception error, int attempts, CancellationToken ct)
    {
        try
        {
            deadLetters.SendAsync(record, error, attempts, ct).GetAwaiter().GetResult();
            DeadLettered(logger, record.Topic, record.Partition.Value, record.Offset.Value, attempts);
            return Outcome.DeadLettered;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception produceError)
        {
            DeadLetterFailed(logger, record.Topic, record.Partition.Value, record.Offset.Value, produceError);
            return Outcome.Retry;
        }
    }

    /// <summary>
    /// Commits the next offset after the last applied record of every partition in the applied prefix. A failure
    /// (for example after the consumer left the group) throws, and the loop replaces the consumer: carrying on would
    /// treat the batch as committed while Kafka still holds the older offsets.
    /// </summary>
    private void Commit(IConsumer<byte[], byte[]> consumer, List<ConsumeResult<byte[], byte[]>> batch, int applied)
    {
        if (applied == 0) return;
        var offsets = batch.Take(applied).GroupBy(record => record.TopicPartition)
            .Select(partition => new TopicPartitionOffset(partition.Key, partition.Max(record => record.Offset.Value) + 1))
            .ToList();
        try
        {
            consumer.Commit(offsets);
        }
        catch (KafkaException error)
        {
            CommitFailed(logger, error.Error.Code, error.Error.Reason);
            throw;
        }
    }

    /// <summary>
    /// The consumer's position is past every record of the batch, so the unapplied ones are sought back; skipping
    /// the commit alone would never deliver them again. A partition revoked meanwhile resumes at its new owner from
    /// the committed offset. A failed seek throws, and the loop replaces the consumer.
    /// </summary>
    private static void Rewind(IConsumer<byte[], byte[]> consumer, List<ConsumeResult<byte[], byte[]>> batch, int applied)
    {
        var assigned = consumer.Assignment.ToHashSet();
        foreach (var partition in batch.Skip(applied).GroupBy(record => record.TopicPartition))
            if (assigned.Contains(partition.Key))
                consumer.Seek(new TopicPartitionOffset(partition.Key, partition.Min(record => record.Offset.Value)));
    }

    private IConsumer<byte[], byte[]> Build() =>
        new ConsumerBuilder<byte[], byte[]>(new ConsumerConfig
            {
                BootstrapServers = options.BootstrapServers,
                GroupId = options.GroupId,
                ClientId = "reporting",
                EnableAutoCommit = false,
                EnableAutoOffsetStore = false,
                AutoOffsetReset = AutoOffsetReset.Earliest
            })
            .SetLogHandler((_, log) => ClientLog(logger, log.Facility, log.Message))
            .SetErrorHandler((_, error) => ClientError(logger, error.Code, error.Reason))
            .Build();

    /// <summary>Leaves the group so its partitions move at once; bounded, because the host's shutdown budget is 25 s.</summary>
    private void Close(IConsumer<byte[], byte[]> consumer)
    {
        lag.Reset();
        try
        {
            if (!Task.Run(consumer.Close).Wait(CloseTimeout))
            {
                CloseTimedOut(logger);
                return;
            }
        }
        catch (AggregateException error)
        {
            CloseFailed(logger, error.InnerException ?? error);
        }
        consumer.Dispose();
    }

    private static void Wait(TimeSpan delay, CancellationToken ct)
    {
        if (ct.WaitHandle.WaitOne(delay)) ct.ThrowIfCancellationRequested();
    }

    [LoggerMessage(1, LogLevel.Debug, "Kafka consumer {Facility}: {Message}")]
    private static partial void ClientLog(ILogger logger, string facility, string message);
    [LoggerMessage(2, LogLevel.Debug, "Kafka consumer error {Code}: {Reason}")]
    private static partial void ClientError(ILogger logger, ErrorCode code, string reason);
    [LoggerMessage(3, LogLevel.Warning, "Consuming failed with {Code}: {Reason}")]
    private static partial void ConsumeFailed(ILogger logger, ErrorCode code, string reason);
    [LoggerMessage(4, LogLevel.Warning, "MongoDB is unavailable; {Topic} [{Partition}] @{Offset} will be consumed again")]
    private static partial void StoreUnavailable(ILogger logger, string topic, int partition, long offset, Exception error);
    [LoggerMessage(5, LogLevel.Warning, "Record {Topic} [{Partition}] @{Offset} failed on attempt {Attempt}")]
    private static partial void AttemptFailed(ILogger logger, string topic, int partition, long offset, int attempt, Exception error);
    [LoggerMessage(6, LogLevel.Error, "Record {Topic} [{Partition}] @{Offset} sent to the dead topic after {Attempts} attempts")]
    private static partial void DeadLettered(ILogger logger, string topic, int partition, long offset, int attempts);
    [LoggerMessage(7, LogLevel.Error, "Record {Topic} [{Partition}] @{Offset} could not be sent to the dead topic; it will be consumed again")]
    private static partial void DeadLetterFailed(ILogger logger, string topic, int partition, long offset, Exception error);
    [LoggerMessage(8, LogLevel.Debug, "No projection handles {Type}; {Topic} [{Partition}] @{Offset} skipped")]
    private static partial void Skipped(ILogger logger, string type, string topic, int partition, long offset);
    private enum Outcome { Applied, DeadLettered, Retry }

    [LoggerMessage(9, LogLevel.Warning, "Offset commit failed with {Code}: {Reason}")]
    private static partial void CommitFailed(ILogger logger, ErrorCode code, string reason);
    [LoggerMessage(10, LogLevel.Error, "Kafka consumer failed; a new consumer resumes from the committed offsets")]
    private static partial void ConsumerRestarting(ILogger logger, Exception error);
    [LoggerMessage(11, LogLevel.Warning, "Creating the read model indexes failed; retrying")]
    private static partial void IndexesFailed(ILogger logger, Exception error);
    [LoggerMessage(12, LogLevel.Warning, "Closing the Kafka consumer timed out")]
    private static partial void CloseTimedOut(ILogger logger);
    [LoggerMessage(13, LogLevel.Warning, "Closing the Kafka consumer failed")]
    private static partial void CloseFailed(ILogger logger, Exception error);
}
