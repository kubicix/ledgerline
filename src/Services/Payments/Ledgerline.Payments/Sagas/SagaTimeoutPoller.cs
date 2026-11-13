using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Features.Refunds;
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
        var payment = await db.Payments.FromSqlRaw("""
            SELECT p.*, p.xmin FROM payments p
            WHERE EXISTS (SELECT 1 FROM saga_timeouts t WHERE t.payment_id = p.id AND t.due_at <= now())
            ORDER BY (SELECT min(t.due_at) FROM saga_timeouts t WHERE t.payment_id = p.id AND t.due_at <= now())
            LIMIT 1 FOR UPDATE OF p SKIP LOCKED
            """).SingleOrDefaultAsync(ct);
        if (payment is null) return;
        var timeout = await db.SagaTimeouts.FromSqlInterpolated($"""
            SELECT * FROM saga_timeouts WHERE payment_id = {payment.Id} AND due_at <= now()
            ORDER BY due_at LIMIT 1 FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(ct);
        if (timeout is null) return;
        if (await WaitForPublicationAsync(db, timeout, ct))
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return;
        }
        crashPoint.Hit("saga.timeout-locked");
        var runner = scope.ServiceProvider.GetRequiredService<SagaRunner>();
        if (timeout.RefundId is { } refund)
            await scope.ServiceProvider.GetRequiredService<RefundRecovery>().RetryAsync(refund, payment, timeout.DurationMs, ct);
        else await runner.ApplyAsync(timeout.PaymentId, new SagaInput.StepTimedOut(timeout.Step), null, ct);
        if (db.Entry(timeout).State != EntityState.Detached)
        {
            db.SagaTimeouts.Remove(timeout);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        runner.OnCommitted();
    }

    private static async Task<bool> WaitForPublicationAsync(PaymentsDbContext db, Infrastructure.Entities.SagaTimeout timeout, CancellationToken ct)
    {
        if (timeout.CommandMessageId is not { } id) return false;
        var command = await db.Set<OutboxMessage>().AsNoTracking().SingleOrDefaultAsync(x => x.MessageId == id, ct);
        // Retention removes only confirmed commands, after every command timeout has elapsed.
        if (command is null) { timeout.CommandMessageId = null; return false; }
        var duration = TimeSpan.FromMilliseconds(timeout.DurationMs);
        if (command.ProcessedAt is not { } published)
        {
            timeout.DueAt = DateTimeOffset.UtcNow + duration;
            return true;
        }
        timeout.CommandMessageId = null;
        timeout.DueAt = published + duration;
        return timeout.DueAt > DateTimeOffset.UtcNow;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Saga timeout polling failed.")]
    private static partial void PollFailed(ILogger logger, Exception error);
}
