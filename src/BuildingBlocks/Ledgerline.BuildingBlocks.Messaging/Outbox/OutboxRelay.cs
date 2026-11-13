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
    private bool publicationFailed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await PublishBatchAsync(stoppingToken)) { }
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
        var head = await db.Set<OutboxMessage>().Where(x => x.ProcessedAt == null).OrderBy(x => x.Sequence)
            .Select(x => (DateTimeOffset?)x.NextAttemptAt).FirstOrDefaultAsync(ct);
        if (head > clock.UtcNow) return false;
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
        try
        {
            await publisher.PublishAsync(row, ct);
        }
        catch (PublishFailedException error)
        {
            row.Attempts++;
            row.LastError = error.Message;
            row.NextAttemptAt = clock.UtcNow.AddMilliseconds(Math.Min(Math.Pow(2, row.Attempts) * 100, 30_000));
            if (!publicationFailed) PublicationFailed(logger, error);
            publicationFailed = true;
            return false;
        }
        crashPoint.Hit("relay.after-publish");
        row.ProcessedAt = clock.UtcNow;
        if (publicationFailed) PublicationRecovered(logger);
        publicationFailed = false;
        return true;
    }

    [LoggerMessage(1, LogLevel.Warning, "Outbox publication failed; pending rows will be retried.")]
    private static partial void PublicationFailed(ILogger logger, Exception error);
    [LoggerMessage(2, LogLevel.Information, "Outbox publication recovered.")]
    private static partial void PublicationRecovered(ILogger logger);
    [LoggerMessage(3, LogLevel.Error, "Outbox batch failed; pending rows remain available for retry.")]
    private static partial void LoopFailed(ILogger logger, Exception error);
}
