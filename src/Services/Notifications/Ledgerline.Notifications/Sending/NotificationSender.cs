using Ledgerline.BuildingBlocks;
using Ledgerline.Notifications.Infrastructure;
using Ledgerline.Notifications.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Notifications.Sending;

public sealed class SenderOptions
{
    public int PollIntervalMs { get; set; } = 500;
    public TimeSpan BackoffUnit { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed partial class NotificationSender(IServiceScopeFactory scopes, SenderOptions options,
    IClock clock, IEmailSender sender, ILogger<NotificationSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.PollIntervalMs));
        do
        {
            try { await SendBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { BatchFailed(logger, error.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SendBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Notifications.FromSqlInterpolated($"""
            SELECT * FROM notifications WHERE status = 'Pending' AND next_attempt_at <= {clock.UtcNow}
            ORDER BY next_attempt_at, id LIMIT 20 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        foreach (var row in rows) await SendAsync(row, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task SendAsync(Notification row, CancellationToken ct)
    {
        try
        {
            await sender.SendAsync(row, ct);
            row.Status = "Sent";
            row.SentAt = clock.UtcNow;
            row.Reason = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            row.Attempts++;
            row.NextAttemptAt = clock.UtcNow.Add(TimeSpan.FromMilliseconds(Math.Min(Math.Pow(2, row.Attempts) * options.BackoffUnit.TotalMilliseconds, 300000)));
            row.Reason = "SmtpFailure";
            if (row.Attempts >= 6) row.Status = "Failed";
            DeliveryFailed(logger, row.Id, error.GetType().Name);
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "Notification {NotificationId} delivery failed ({ErrorType}).")]
    private static partial void DeliveryFailed(ILogger logger, Guid notificationId, string errorType);
    [LoggerMessage(2, LogLevel.Warning, "Notification sender batch failed ({ErrorType}).")]
    private static partial void BatchFailed(ILogger logger, string errorType);
}
