using System.Diagnostics.Metrics;
using System.Text.Json;
using Confluent.Kafka;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Reconciliation.Infrastructure;
using Npgsql;

namespace Ledgerline.Reconciliation.Staging;

/// <summary>
/// Consumes the payment and ledger topics into the staging tables (reconciliation.md section 2), copied from
/// Reporting's consumer loop. Offsets are committed by hand, and only for records whose staging transaction committed:
/// <list type="bullet">
/// <item>Every record is applied in its own database transaction, in partition order. A failure that is not bad data
/// (see <see cref="IsPoison"/>: the database away, misconfigured or not migrated yet, a timeout, anything else) is
/// retried without limit: the applied prefix is committed, every partition of the batch is sought back to its first
/// unapplied record, and the loop backs off and consumes again. No offset is ever committed past such a record.</item>
/// <item>A data failure is retried in place up to <see cref="MaxAttempts"/> times; then the record is poison: it is
/// logged (position and error type, never its content) and counted, and counts as applied. The batch ends at that
/// record and its offset is committed at once. There is no dead topic: the Compose broker creates no topic for
/// Reconciliation, and producing to an unlisted topic fails there.</item>
/// <item>A record whose type no staging consumer handles counts as applied.</item>
/// <item>A failed offset commit replaces the consumer, which resumes from the offsets Kafka really holds.</item>
/// <item>Every committed prefix and every partition assignment is reported to <see cref="StagingProgress"/>.</item>
/// </list>
/// The blocking consumer runs on its own thread, so an absent broker blocks neither host start nor readiness.
/// </summary>
public sealed partial class StagingConsumerLoop : BackgroundService
{
    public const int MaxAttempts = 5;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);
    private static readonly Meter Meter = new("Ledgerline.Reconciliation");
    private static readonly Counter<long> PoisonRecords = Meter.CreateCounter<long>("ledgerline.reconciliation.staging.poison", "{record}",
        "Records skipped after every staging attempt failed on bad data.");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("ledgerline.reconciliation.staging.retries", "{record}",
        "Records left uncommitted, to be consumed again, after a failure that is not bad data.");
    private readonly StagingOptions options;
    private readonly IServiceScopeFactory scopes;
    private readonly StagingProgress progress;
    private readonly ILogger<StagingConsumerLoop> logger;
    private readonly Dictionary<string, IStagingConsumer> consumers;

    public StagingConsumerLoop(StagingOptions options, IServiceScopeFactory scopes, IEnumerable<IStagingConsumer> consumers,
        StagingProgress progress, ILogger<StagingConsumerLoop> logger)
    {
        this.options = options;
        this.scopes = scopes;
        this.progress = progress;
        this.logger = logger;
        this.consumers = consumers
            .SelectMany(consumer => consumer.MessageTypes.Select(type => (Type: type, Consumer: consumer)))
            .ToDictionary(x => x.Type, x => x.Consumer, StringComparer.Ordinal);
    }

    /// <summary>
    /// Bad data, which no retry can fix: an envelope or payload that does not parse, convert or validate
    /// (<see cref="JsonException"/>, or <see cref="InvalidDataException"/>, which <see cref="ReadPayload{T}"/> also
    /// raises for an <see cref="ArgumentException"/>, <see cref="FormatException"/> or <see cref="OverflowException"/>
    /// while reading a payload), or a <see cref="PostgresException"/> of SQLSTATE class 22 (data exception) or 23
    /// (integrity constraint violation). Database exceptions are classified first, so a driver error is never taken for
    /// bad data. Every other failure is not the record's fault and is retried without committing: connection failures,
    /// timeouts, an <see cref="NpgsqlException"/> without a server error, failed authentication (class 28), a missing
    /// database (3D000) or table (42P01, before migrate), classes 53 and 57, serialization failures and deadlocks
    /// (40001, 40P01), any other class, and any other exception, an argument error outside payload reading (for
    /// example a bad connection string) included.
    /// </summary>
    public static bool IsPoison(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres) return postgres.SqlState.StartsWith("22", StringComparison.Ordinal) ||
                postgres.SqlState.StartsWith("23", StringComparison.Ordinal);
            if (current is NpgsqlException) return false;
            if (current is JsonException or InvalidDataException) return true;
        }
        return false;
    }

    /// <summary>
    /// Runs the parse and convert step of a staging consumer. An argument, format or overflow error there comes from the
    /// record's data, so it is rethrown as <see cref="InvalidDataException"/> and the record becomes poison; the same
    /// exception types raised later, by the database step, stay retryable.
    /// </summary>
    public static T ReadPayload<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The record's payload could not be read.", error);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var consumer = Build();
                try
                {
                    consumer.Subscribe(options.Topics);
                    Poll(consumer, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception error)
                {
                    // A fatal client error, a failed commit or a failed seek: a new consumer resumes from the
                    // committed offsets. Records staged since then are staged again (idempotently).
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

    private void Poll(IConsumer<byte[], byte[]> consumer, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = NextBatch(consumer, ct);
            if (batch.Count == 0) continue;
            var (done, retry) = Apply(batch, ct);
            Commit(consumer, batch, done);
            if (done < batch.Count) Rewind(consumer, batch, done);
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
    /// off before consuming the rest again. The batch also ends, without backoff, right after a poison record.
    /// </summary>
    private (int Done, bool Retry) Apply(List<ConsumeResult<byte[], byte[]>> batch, CancellationToken ct)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            switch (TryApply(batch[i], ct))
            {
                case Outcome.Retry: return (i, true);
                case Outcome.Poison: return (i + 1, false);
            }
        }
        return (batch.Count, false);
    }

    /// <summary><see cref="Outcome.Retry"/> when the record must be consumed again: it failed for a reason other than its data.</summary>
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
            catch (Exception error) when (!IsPoison(error))
            {
                Retries.Add(1, new KeyValuePair<string, object?>("topic", record.Topic));
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
                PoisonRecords.Add(1, new KeyValuePair<string, object?>("topic", record.Topic));
                Poisoned(logger, record.Topic, record.Partition.Value, record.Offset.Value, attempt, error.GetType().Name);
                return Outcome.Poison;
            }
        }
    }

    /// <summary>Stages one record in its own transaction; the transaction has committed when this returns.</summary>
    private void ApplyOnce(ConsumeResult<byte[], byte[]> record, CancellationToken ct)
    {
        var envelope = EnvelopeSerializer.Deserialize(record.Message.Value);
        if (string.IsNullOrEmpty(envelope.Type)) throw new InvalidDataException("The envelope has no type.");
        if (envelope.Payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The envelope has no payload object.");
        if (!consumers.TryGetValue(envelope.Type, out var consumer))
        {
            Skipped(logger, envelope.Type, record.Topic, record.Partition.Value, record.Offset.Value);
            return;
        }
        ApplyAsync(consumer, envelope, ct).GetAwaiter().GetResult();
    }

    private async Task ApplyAsync(IStagingConsumer consumer, MessageEnvelope envelope, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await consumer.ApplyAsync(envelope, db, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Commits the next offset after the last applied record of every partition in the applied prefix, then reports
    /// those records to <see cref="StagingProgress"/>. A failure (for example after the consumer left the group) throws,
    /// and the loop replaces the consumer: carrying on would treat the batch as committed while Kafka still holds the
    /// older offsets.
    /// </summary>
    private void Commit(IConsumer<byte[], byte[]> consumer, List<ConsumeResult<byte[], byte[]>> batch, int applied)
    {
        if (applied == 0) return;
        var lasts = batch.Take(applied).GroupBy(record => record.TopicPartition)
            .Select(partition => partition.MaxBy(record => record.Offset.Value)!)
            .ToList();
        try
        {
            consumer.Commit(lasts.Select(record => new TopicPartitionOffset(record.TopicPartition, record.Offset.Value + 1)));
        }
        catch (KafkaException error)
        {
            CommitFailed(logger, error.Error.Code, error.Error.Reason);
            throw;
        }
        foreach (var record in lasts)
            progress.Committed(record.TopicPartition,
                new PartitionProgress(record.Offset.Value, new DateTimeOffset(record.Message.Timestamp.UtcDateTime, TimeSpan.Zero)));
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
                ClientId = "reconciliation",
                EnableAutoCommit = false,
                EnableAutoOffsetStore = false,
                AutoOffsetReset = AutoOffsetReset.Earliest
            })
            .SetPartitionsAssignedHandler((_, partitions) => progress.Assigned(partitions))
            .SetPartitionsRevokedHandler((_, partitions) => progress.Revoked(partitions.Select(x => x.TopicPartition)))
            .SetPartitionsLostHandler((_, partitions) => progress.Revoked(partitions.Select(x => x.TopicPartition)))
            .SetLogHandler((_, log) => ClientLog(logger, log.Facility, log.Message))
            .SetErrorHandler((_, error) => ClientError(logger, error.Code, error.Reason))
            .Build();

    /// <summary>Leaves the group so its partitions move at once; bounded, because the host's shutdown budget is 25 s.</summary>
    private void Close(IConsumer<byte[], byte[]> consumer)
    {
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
        finally
        {
            progress.Released();
        }
        consumer.Dispose();
    }

    private static void Wait(TimeSpan delay, CancellationToken ct)
    {
        if (ct.WaitHandle.WaitOne(delay)) ct.ThrowIfCancellationRequested();
    }

    private enum Outcome { Applied, Poison, Retry }

    [LoggerMessage(1, LogLevel.Debug, "Kafka consumer {Facility}: {Message}")]
    private static partial void ClientLog(ILogger logger, string facility, string message);
    [LoggerMessage(2, LogLevel.Debug, "Kafka consumer error {Code}: {Reason}")]
    private static partial void ClientError(ILogger logger, ErrorCode code, string reason);
    [LoggerMessage(3, LogLevel.Warning, "Consuming failed with {Code}: {Reason}")]
    private static partial void ConsumeFailed(ILogger logger, ErrorCode code, string reason);
    [LoggerMessage(4, LogLevel.Warning, "Staging {Topic} [{Partition}] @{Offset} failed for a reason other than its data; it will be consumed again")]
    private static partial void StoreUnavailable(ILogger logger, string topic, int partition, long offset, Exception error);
    [LoggerMessage(5, LogLevel.Warning, "Record {Topic} [{Partition}] @{Offset} failed on attempt {Attempt}")]
    private static partial void AttemptFailed(ILogger logger, string topic, int partition, long offset, int attempt, Exception error);
    [LoggerMessage(6, LogLevel.Error, "Record {Topic} [{Partition}] @{Offset} skipped as poison after {Attempts} attempts; last error {ErrorType}")]
    private static partial void Poisoned(ILogger logger, string topic, int partition, long offset, int attempts, string errorType);
    [LoggerMessage(7, LogLevel.Debug, "No staging consumer handles {Type}; {Topic} [{Partition}] @{Offset} skipped")]
    private static partial void Skipped(ILogger logger, string type, string topic, int partition, long offset);
    [LoggerMessage(8, LogLevel.Warning, "Offset commit failed with {Code}: {Reason}")]
    private static partial void CommitFailed(ILogger logger, ErrorCode code, string reason);
    [LoggerMessage(9, LogLevel.Error, "Kafka consumer failed; a new consumer resumes from the committed offsets")]
    private static partial void ConsumerRestarting(ILogger logger, Exception error);
    [LoggerMessage(10, LogLevel.Warning, "Closing the Kafka consumer timed out")]
    private static partial void CloseTimedOut(ILogger logger);
    [LoggerMessage(11, LogLevel.Warning, "Closing the Kafka consumer failed")]
    private static partial void CloseFailed(ILogger logger, Exception error);
}
