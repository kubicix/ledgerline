using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Sagas;

public sealed partial class SagaTimeoutPoller(IServiceScopeFactory scopes, TimeoutOptions options,
    ICrashPoint crashPoint, ILogger<SagaTimeoutPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                crashPoint.Hit("saga.before-timeout-poll");
                await PollOnceAsync(stoppingToken);
            }
            catch (HostAbortedException) { return; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { PollFailed(logger, error); }
            await Task.Delay(TimeSpan.FromMilliseconds(options.PollIntervalMs), stoppingToken);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var timeout = await db.SagaTimeouts.FromSqlRaw("""
            SELECT * FROM saga_timeouts WHERE due_at <= now()
            ORDER BY due_at LIMIT 1 FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(ct);
        if (timeout is null) return;
        var runner = scope.ServiceProvider.GetRequiredService<SagaRunner>();
        await runner.ApplyAsync(timeout.PaymentId, new SagaInput.StepTimedOut(timeout.Step), null, ct);
        if (db.Entry(timeout).State != EntityState.Detached)
        {
            db.SagaTimeouts.Remove(timeout);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        runner.OnCommitted();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Saga timeout polling failed.")]
    private static partial void PollFailed(ILogger logger, Exception error);
}
