using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledgerline.BuildingBlocks.Messaging;

public sealed partial class MessagingRetentionJob<TDbContext>(IServiceScopeFactory scopes, RetentionOptions options,
    IClock clock, ILogger<MessagingRetentionJob<TDbContext>> logger) : BackgroundService where TDbContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await CleanAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { CleanupFailed(logger, error); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var cutoff = clock.UtcNow.AddDays(-options.Days);
        await db.Set<OutboxMessage>().Where(x => x.ProcessedAt != null && x.ProcessedAt < cutoff).ExecuteDeleteAsync(ct);
        await db.Set<InboxMessage>().Where(x => x.ReceivedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    [LoggerMessage(1, LogLevel.Warning, "Messaging retention cleanup failed; rows are retained until the next run.")]
    private static partial void CleanupFailed(ILogger logger, Exception error);
}

public sealed class RetentionOptions
{
    public int Days { get; set; } = 7;
}
