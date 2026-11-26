using System.Diagnostics;
using Ledgerline.BuildingBlocks.Messaging.Diagnostics;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledgerline.BuildingBlocks.Messaging.Outbox;

public sealed partial class OutboxRelay<TDbContext>(IServiceScopeFactory scopes, IOutboxPublisher publisher,
    OutboxSignal signal, RelayOptions options, IClock clock, ICrashPoint crashPoint,
    ILogger<OutboxRelay<TDbContext>> logger) : BackgroundService where TDbContext : DbContext
{
    private static readonly TimeSpan PendingRefreshInterval = TimeSpan.FromSeconds(1);
    private readonly MessagingMetrics metrics = new(options.Service);
    private bool publicationFailed;
    private long pendingCountedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool more;
                do
                {
                    more = await PublishBatchAsync(stoppingToken);
                    await RefreshPendingAsync(stoppingToken);
                } while (more);
            }
            catch (HostAbortedException) { return; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { LoopFailed(logger, error); }
            await signal.WaitAsync(TimeSpan.FromMilliseconds(options.PollIntervalMs), stoppingToken);
        }
    }

    private async Task<bool> PublishBatchAsync(CancellationToken ct)
    {
        crashPoint.Hit("relay.before-batch");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The head gate uses the same transaction now() as the batch select, so a head row that is not yet due is never passed.
        var headDue = await db.Database.SqlQuery<bool>($"""
            SELECT next_attempt_at <= now() AS "Value" FROM outbox_messages
            WHERE processed_at IS NULL ORDER BY sequence LIMIT 1
            """).ToListAsync(ct);
        if (headDue is [false]) return false;
        var rows = await db.Set<OutboxMessage>().FromSql($"""
            SELECT * FROM outbox_messages
            WHERE processed_at IS NULL AND next_attempt_at <= now()
            ORDER BY sequence LIMIT {options.BatchSize} FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        var succeeded = true;
        foreach (var row in rows)
        {
            if (await PublishOneAsync(row, ct)) continue;
            succeeded = false;
            break;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return succeeded && rows.Count == options.BatchSize;
    }

    private async Task<bool> PublishOneAsync(OutboxMessage row, CancellationToken ct)
    {
        crashPoint.Hit("relay.before-publish");
        // The PRODUCER span covers only the publish and its broker confirm; it never changes what is published or when.
        using (var activity = MessagingActivitySource.StartPublish(row))
        {
            try
            {
                await publisher.PublishAsync(row, ct);
            }
            catch (PublishFailedException error)
            {
                MessagingActivitySource.RecordError(activity, error);
                row.Attempts++;
                row.LastError = error.Message;
                row.NextAttemptAt = clock.UtcNow.AddMilliseconds(Math.Min(Math.Pow(2, row.Attempts) * 100, 30_000));
                if (!publicationFailed) PublicationFailed(logger, error);
                publicationFailed = true;
                return false;
            }
        }
        // OccurredAt is set at enqueue inside the committing transaction, so it stands in for the commit time.
        metrics.RecordLag(clock.UtcNow - row.OccurredAt);
        crashPoint.Hit("relay.after-publish");
        row.ProcessedAt = clock.UtcNow;
        if (publicationFailed) PublicationRecovered(logger);
        publicationFailed = false;
        return true;
    }

    /// <summary>
    /// Records the backlog for the pending gauge at most once per interval, in its own scope and outside the publish
    /// transaction, so it never changes what is published or when. During a broker outage the batch returns early and
    /// this still runs, so the gauge rises.
    /// </summary>
    private async Task RefreshPendingAsync(CancellationToken ct)
    {
        if (pendingCountedAt != 0 && Stopwatch.GetElapsedTime(pendingCountedAt) < PendingRefreshInterval) return;
        pendingCountedAt = Stopwatch.GetTimestamp();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
            metrics.RecordPending(await db.Set<OutboxMessage>().LongCountAsync(x => x.ProcessedAt == null, ct));
        }
        catch (Exception error) when (error is not OperationCanceledException) { PendingCountFailed(logger, error); }
    }

    [LoggerMessage(1, LogLevel.Warning, "Outbox publication failed; pending rows will be retried.")]
    private static partial void PublicationFailed(ILogger logger, Exception error);
    [LoggerMessage(2, LogLevel.Information, "Outbox publication recovered.")]
    private static partial void PublicationRecovered(ILogger logger);
    [LoggerMessage(3, LogLevel.Error, "Outbox batch failed; pending rows remain available for retry.")]
    private static partial void LoopFailed(ILogger logger, Exception error);
    [LoggerMessage(4, LogLevel.Warning, "Outbox pending count failed; the pending gauge keeps its last value.")]
    private static partial void PendingCountFailed(ILogger logger, Exception error);
}
