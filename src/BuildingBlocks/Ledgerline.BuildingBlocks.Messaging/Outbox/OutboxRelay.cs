using System.Diagnostics;
using Ledgerline.BuildingBlocks.Messaging.Diagnostics;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledgerline.BuildingBlocks.Messaging.Outbox;

/// <summary>
/// Relays outbox rows to their brokers. It runs one loop per registered <see cref="IOutboxPublisher"/>, and each loop
/// sees only the rows of its own <c>transport</c>: head gate, batch, backoff and metrics. A broker that is down
/// therefore never holds back the rows of another transport.
/// </summary>
public sealed partial class OutboxRelay<TDbContext> : BackgroundService where TDbContext : DbContext
{
    private static readonly TimeSpan PendingRefreshInterval = TimeSpan.FromSeconds(1);
    private readonly IServiceScopeFactory scopes;
    private readonly IOutboxPublisher[] publishers;
    private readonly OutboxSignal signal;
    private readonly RelayOptions options;
    private readonly IClock clock;
    private readonly ICrashPoint crashPoint;
    private readonly ILogger<OutboxRelay<TDbContext>> logger;

    public OutboxRelay(IServiceScopeFactory scopes, IEnumerable<IOutboxPublisher> publishers, OutboxSignal signal,
        RelayOptions options, IClock clock, ICrashPoint crashPoint, ILogger<OutboxRelay<TDbContext>> logger)
    {
        this.publishers = publishers.ToArray();
        if (this.publishers.Length == 0) throw new InvalidOperationException("The outbox relay needs at least one publisher.");
        if (this.publishers.Select(x => x.Transport).Distinct(StringComparer.Ordinal).Count() != this.publishers.Length)
            throw new InvalidOperationException("Each outbox transport must have exactly one publisher.");
        this.scopes = scopes;
        this.signal = signal;
        this.options = options;
        this.clock = clock;
        this.crashPoint = crashPoint;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A crash point in one loop stops the whole relay, as it did with a single loop.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await Task.WhenAll(publishers.Select(publisher =>
            Task.Run(() => RunLoopAsync(new Loop(publisher, options.Service), stopping), CancellationToken.None)));
    }

    private async Task RunLoopAsync(Loop loop, CancellationTokenSource stopping)
    {
        var ct = stopping.Token;
        using var wakeUp = signal.Subscribe();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool more;
                do
                {
                    more = await PublishBatchAsync(loop, ct);
                    await RefreshPendingAsync(loop, ct);
                } while (more);
                await wakeUp.WaitAsync(TimeSpan.FromMilliseconds(options.PollIntervalMs), ct);
            }
            catch (HostAbortedException)
            {
                await stopping.CancelAsync();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                LoopFailed(logger, loop.Publisher.Transport, error);
                await WaitAfterFailureAsync(wakeUp, ct);
            }
        }
    }

    private async Task WaitAfterFailureAsync(OutboxSignal.Subscription wakeUp, CancellationToken ct)
    {
        try { await wakeUp.WaitAsync(TimeSpan.FromMilliseconds(options.PollIntervalMs), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task<bool> PublishBatchAsync(Loop loop, CancellationToken ct)
    {
        crashPoint.Hit("relay.before-batch");
        var transport = loop.Publisher.Transport;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The head gate uses the same transaction now() as the batch select, so a head row that is not yet due is never passed.
        var headDue = await db.Database.SqlQuery<bool>($"""
            SELECT next_attempt_at <= now() AS "Value" FROM outbox_messages
            WHERE processed_at IS NULL AND transport = {transport} ORDER BY sequence LIMIT 1
            """).ToListAsync(ct);
        if (headDue is [false]) return false;
        var rows = await db.Set<OutboxMessage>().FromSql($"""
            SELECT * FROM outbox_messages
            WHERE processed_at IS NULL AND transport = {transport} AND next_attempt_at <= now()
            ORDER BY sequence LIMIT {options.BatchSize} FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        var succeeded = true;
        foreach (var row in rows)
        {
            if (await PublishOneAsync(loop, row, ct)) continue;
            succeeded = false;
            break;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return succeeded && rows.Count == options.BatchSize;
    }

    private async Task<bool> PublishOneAsync(Loop loop, OutboxMessage row, CancellationToken ct)
    {
        crashPoint.Hit("relay.before-publish");
        // The PRODUCER span covers only the publish and its broker confirm; it never changes what is published or when.
        using (var activity = MessagingActivitySource.StartPublish(row))
        {
            try
            {
                await loop.Publisher.PublishAsync(row, ct);
            }
            catch (PublishFailedException error)
            {
                MessagingActivitySource.RecordError(activity, error);
                row.Attempts++;
                row.LastError = error.Message;
                row.NextAttemptAt = clock.UtcNow.AddMilliseconds(Math.Min(Math.Pow(2, row.Attempts) * 100, 30_000));
                if (!loop.PublicationFailed) PublicationFailed(logger, loop.Publisher.Transport, error);
                loop.PublicationFailed = true;
                return false;
            }
        }
        // OccurredAt is set at enqueue inside the committing transaction, so it stands in for the commit time.
        loop.Metrics.RecordLag(clock.UtcNow - row.OccurredAt);
        crashPoint.Hit("relay.after-publish");
        row.ProcessedAt = clock.UtcNow;
        if (loop.PublicationFailed) PublicationRecovered(logger, loop.Publisher.Transport);
        loop.PublicationFailed = false;
        return true;
    }

    /// <summary>
    /// Records the backlog of the loop's transport for the pending gauge at most once per interval, in its own scope and
    /// outside the publish transaction, so it never changes what is published or when. During a broker outage the batch
    /// returns early and this still runs, so the gauge rises.
    /// </summary>
    private async Task RefreshPendingAsync(Loop loop, CancellationToken ct)
    {
        if (loop.PendingCountedAt != 0 && Stopwatch.GetElapsedTime(loop.PendingCountedAt) < PendingRefreshInterval) return;
        loop.PendingCountedAt = Stopwatch.GetTimestamp();
        var transport = loop.Publisher.Transport;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
            loop.Metrics.RecordPending(await db.Set<OutboxMessage>()
                .LongCountAsync(x => x.ProcessedAt == null && x.Transport == transport, ct));
        }
        catch (Exception error) when (error is not OperationCanceledException) { PendingCountFailed(logger, transport, error); }
    }

    /// <summary>The state of one transport's loop; only that loop touches it.</summary>
    private sealed class Loop(IOutboxPublisher publisher, string service)
    {
        public IOutboxPublisher Publisher { get; } = publisher;
        public MessagingMetrics Metrics { get; } = new(service, publisher.Transport);
        public bool PublicationFailed { get; set; }
        public long PendingCountedAt { get; set; }
    }

    [LoggerMessage(1, LogLevel.Warning, "Outbox publication to {Transport} failed; pending rows will be retried.")]
    private static partial void PublicationFailed(ILogger logger, string transport, Exception error);
    [LoggerMessage(2, LogLevel.Information, "Outbox publication to {Transport} recovered.")]
    private static partial void PublicationRecovered(ILogger logger, string transport);
    [LoggerMessage(3, LogLevel.Error, "Outbox batch for {Transport} failed; pending rows remain available for retry.")]
    private static partial void LoopFailed(ILogger logger, string transport, Exception error);
    [LoggerMessage(4, LogLevel.Warning, "Outbox pending count for {Transport} failed; the pending gauge keeps its last value.")]
    private static partial void PendingCountFailed(ILogger logger, string transport, Exception error);
}
