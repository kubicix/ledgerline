using System.Text.Json;
using Ledgerline.BuildingBlocks.Messaging.Diagnostics;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Ledgerline.BuildingBlocks.Messaging.Consuming;

public sealed partial class ConsumerHost<TDbContext>(RabbitMqConnection connection, HandlerRegistry handlers,
    IServiceScopeFactory scopes, IAckStrategy acknowledgments, ConsumerOptions options, IClock clock, ICrashPoint crashPoint,
    IHostApplicationLifetime lifetime, ILogger<ConsumerHost<TDbContext>> logger) : BackgroundService where TDbContext : DbContext
{
    private readonly MessagingMetrics metrics = new(options.Service);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The drain starts when shutdown starts (ApplicationStopping), not when this service's StopAsync is reached, so it
        // runs alongside the web server's stop. StopAsync then only awaits this same drain.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
        var stop = stopping.Token;
        while (!stop.IsCancellationRequested)
        {
            try { await ConsumeChannelAsync(stop); }
            catch (HostAbortedException) { return; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                HandlerFailed(logger, error);
                try { await Task.Delay(TimeSpan.FromMilliseconds(250), stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task ConsumeChannelAsync(CancellationToken stoppingToken)
    {
        crashPoint.Hit("consumer.before-channel");
        using var forceStop = new CancellationTokenSource();
        await using var channel = await connection.CreateChannelAsync(false, stoppingToken);
        await Topology.DeclareAsync(channel, options.Service, handlers.TypeNames, options.RetryTtlMs, stoppingToken);
        await channel.BasicQosAsync(0, options.Prefetch, false, stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) => { closed.TrySetResult(); return Task.CompletedTask; };
        consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(channel, delivery, forceStop.Token);
        consumer.UnregisteredAsync += (_, _) => { drained.TrySetResult(); return Task.CompletedTask; };
        var tag = await channel.BasicConsumeAsync(Topology.InboxQueue(options.Service), false, consumer, stoppingToken);
        try { await closed.Task.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await DrainAsync(channel, tag, drained.Task, forceStop); }
    }

    private static async Task DrainAsync(IChannel channel, string tag, Task drained, CancellationTokenSource forceStop)
    {
        forceStop.CancelAfter(TimeSpan.FromSeconds(20));
        if (!channel.IsOpen) return;
        await channel.BasicCancelAsync(tag);
        try { await drained.WaitAsync(forceStop.Token); }
        catch (OperationCanceledException) when (forceStop.IsCancellationRequested) { }
    }

    private async Task HandleDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        try { crashPoint.Hit("consumer.before-delivery"); }
        catch (HostAbortedException) { return; }
        var envelope = ReadEnvelope(delivery.Body);
        if (envelope is null || !handlers.Contains(envelope.Type))
        {
            await ForwardSafelyAsync(channel, delivery, Topology.DeadQueue(options.Service), 1,
                envelope is null ? "invalid-envelope" : "unknown-type", ct);
            return;
        }
        if (!ReadAttempt(delivery.BasicProperties.Headers, out var attempt))
        {
            await ForwardSafelyAsync(channel, delivery, Topology.DeadQueue(options.Service), 1, "invalid-attempt", ct);
            return;
        }
        try
        {
            await ProcessAsync(envelope, attempt, ct);
            await acknowledgments.AckAsync(channel, delivery.DeliveryTag, ct);
        }
        catch (HostAbortedException) { return; }
        catch (Exception error)
        {
            HandlerFailed(logger, error);
            var retry = attempt < options.MaxAttempts;
            await ForwardSafelyAsync(channel, delivery,
                retry ? Topology.RetryQueue(options.Service) : Topology.DeadQueue(options.Service),
                retry ? attempt + 1 : attempt, retry ? null : "max-attempts", ct);
        }
    }

    private static bool ReadAttempt(IDictionary<string, object?>? headers, out int attempt)
    {
        attempt = 1;
        if (headers?.TryGetValue("x-ledgerline-attempt", out var value) != true) return true;
        attempt = value switch
        {
            byte number => number,
            short number => number,
            int number => number,
            long number when number is > 0 and <= int.MaxValue => (int)number,
            _ => 0
        };
        return attempt > 0;
    }

    private static MessageEnvelope? ReadEnvelope(ReadOnlyMemory<byte> body)
    {
        try
        {
            var envelope = EnvelopeSerializer.Deserialize(body.Span);
            return envelope.MessageId == Guid.Empty || envelope.CorrelationId == Guid.Empty
                || string.IsNullOrWhiteSpace(envelope.Type) || envelope.Payload.ValueKind == JsonValueKind.Undefined ? null : envelope;
        }
        catch (JsonException) { return null; }
    }

    private async Task ForwardSafelyAsync(IChannel channel, BasicDeliverEventArgs delivery, string queue,
        int attempt, string? deadReason, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var publishing = await connection.CreateChannelAsync(true, timeout.Token);
            var properties = new BasicProperties(delivery.BasicProperties)
            {
                Headers = delivery.BasicProperties.Headers is { } headers
                    ? new Dictionary<string, object?>(headers) : new Dictionary<string, object?>()
            };
            properties.Headers["x-ledgerline-attempt"] = attempt;
            if (deadReason is not null) properties.Headers["x-ledgerline-dead-reason"] = deadReason;
            await publishing.BasicPublishAsync("", queue, true, properties, delivery.Body, timeout.Token);
            await acknowledgments.AckAsync(channel, delivery.DeliveryTag, ct);
            if (deadReason is null) metrics.Retried();
            else metrics.DeadLettered();
        }
        catch (Exception error)
        {
            HandlerFailed(logger, error);
            if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, false, true, CancellationToken.None);
        }
    }

    private async Task ProcessAsync(MessageEnvelope envelope, int attempt, CancellationToken ct)
    {
        using var activity = MessagingActivitySource.StartProcess(envelope, Topology.InboxQueue(options.Service));
        try { await ProcessInTransactionAsync(envelope, attempt, ct); }
        catch (Exception error) when (MessagingActivitySource.RecordError(activity, error)) { throw; }
    }

    private async Task ProcessInTransactionAsync(MessageEnvelope envelope, int attempt, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inbox_messages (message_id, consumer, type, received_at)
            VALUES ({envelope.MessageId}, {options.Service}, {envelope.Type}, {clock.UtcNow})
            ON CONFLICT DO NOTHING
            """, ct);
        if (inserted == 0)
        {
            await transaction.RollbackAsync(ct);
            metrics.Duplicate();
            return;
        }
        var context = new MessageContext(envelope.MessageId, envelope.CorrelationId, envelope.CausationId, attempt);
        await handlers.InvokeAsync(envelope, scope.ServiceProvider, context, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        foreach (var hook in scope.ServiceProvider.GetServices<IPostCommitHook>()) hook.OnCommitted();
    }

    [LoggerMessage(1, LogLevel.Warning, "Message handling failed; delivery remains available for redelivery.")]
    private static partial void HandlerFailed(ILogger logger, Exception error);
}

public sealed class ConsumerOptions
{
    public string Service { get; set; } = "";
    public ushort Prefetch { get; set; } = 16;
    public int MaxAttempts { get; set; } = 5;
    public int RetryTtlMs { get; set; } = 5000;
}
