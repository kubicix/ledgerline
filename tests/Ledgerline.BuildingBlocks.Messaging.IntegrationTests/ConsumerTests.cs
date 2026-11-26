using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.BuildingBlocks.Messaging.IntegrationTests.Support;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

public sealed class ConsumerTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Malformed_attempt_headers_dead_letter_without_stalling_prefetch_one()
    {
        var (cs, ids) = await CreateDatabaseAsync(1);
        var service = "headers" + Ids.New().ToString("N");
        using var host = CreateHost(cs, service, prefetch: 1);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await WaitConsumersAsync(service, 1);
        await using var connection = new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri });
        await using var channel = await connection.CreateChannelAsync(true, TestContext.Current.CancellationToken);
        object[] invalid = ["garbage", System.Text.Encoding.UTF8.GetBytes("1"), long.MaxValue, 0, -1, true];
        foreach (var header in invalid)
            await PublishDeliveryAsync(channel, service, ids[0], "bad", header, omitIdentity: false);
        await PublishDeliveryAsync(channel, service, ids[0], "renamed", 1, omitIdentity: false);
        var reasons = new List<string>();
        await Eventually.ShouldAsync(async () =>
        {
            var dead = await channel.BasicGetAsync(Topology.DeadQueue(service), true, TestContext.Current.CancellationToken);
            if (dead is not null) reasons.Add(System.Text.Encoding.UTF8.GetString((byte[])dead.BasicProperties.Headers!["x-ledgerline-dead-reason"]!));
            return reasons.Count == invalid.Length;
        });
        reasons.ShouldAllBe(reason => reason == "invalid-attempt");
        await Eventually.ShouldAsync(async () => await InboxCountAsync(cs) == 1);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await AssertEffectsAsync(cs, ids);
    }

    [Fact]
    public async Task Missing_message_identities_are_dead_lettered_without_business_effects()
    {
        var (cs, ids) = await CreateDatabaseAsync(2);
        var service = "identities" + Ids.New().ToString("N");
        using var host = CreateHost(cs, service, prefetch: 1);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await WaitConsumersAsync(service, 1);
        await using var connection = new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri });
        await using var channel = await connection.CreateChannelAsync(true, TestContext.Current.CancellationToken);
        foreach (var id in ids) await PublishDeliveryAsync(channel, service, id, "bad", 1, omitIdentity: true);
        var deadCount = 0;
        await Eventually.ShouldAsync(async () =>
        {
            var dead = await channel.BasicGetAsync(Topology.DeadQueue(service), true, TestContext.Current.CancellationToken);
            if (dead is not null) deadCount++;
            return deadCount == 2;
        });
        await host.StopAsync(TestContext.Current.CancellationToken);
        await using var db = OpenDb(cs);
        (await db.Widgets.ToListAsync(TestContext.Current.CancellationToken)).ShouldAllBe(x => x.Name == "original");
        (await db.Set<InboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.Set<OutboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    private static async Task PublishDeliveryAsync(IChannel channel, string service, Guid widgetId,
        string name, object attempt, bool omitIdentity)
    {
        var payload = EnvelopeSerializer.ToPayload(new RenameWidget(widgetId, name));
        var body = omitIdentity
            ? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { type = "consumer.command.rename-widget.v1", occurredAt = DateTimeOffset.UtcNow, correlationId = widgetId, payload })
            : EnvelopeSerializer.Serialize(new MessageEnvelope(Ids.New(), "consumer.command.rename-widget.v1", DateTimeOffset.UtcNow,
                widgetId, null, null, payload));
        var properties = new BasicProperties { Headers = new Dictionary<string, object?> { ["x-ledgerline-attempt"] = attempt } };
        await channel.BasicPublishAsync("", Topology.InboxQueue(service), true, properties, body, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Message_is_handled_and_acknowledged()
    {
        var (cs, ids) = await CreateDatabaseAsync(1);
        var service = "consumer" + Ids.New().ToString("N");
        using var host = CreateHost(cs, service);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await WaitConsumersAsync(service, 1);
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "consumer.event.#");
        await probe.PublishAsync(new RenameWidget(ids[0], "renamed"), Ids.New());
        await Eventually.ShouldAsync(async () => await InboxCountAsync(cs) == 1);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await AssertEffectsAsync(cs, ids);
        await AssertQueueEmptyAsync(service);
    }

    [Fact]
    public async Task Same_message_delivered_twice_is_handled_once()
    {
        var (cs, ids) = await CreateDatabaseAsync(1);
        var service = "consumer" + Ids.New().ToString("N");
        using var metrics = MetricRecorder.ForMeter("Ledgerline");
        using var host = CreateHost(cs, service);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await WaitConsumersAsync(service, 1);
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "consumer.event.#");
        var messageId = Ids.New();
        await probe.PublishAsync(new RenameWidget(ids[0], "renamed"), ids[0], messageId);
        await probe.PublishAsync(new RenameWidget(ids[0], "renamed"), ids[0], messageId);
        await Eventually.ShouldAsync(async () => await InboxCountAsync(cs) == 1);
        await WaitQueueDrainedAsync(service);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await AssertEffectsAsync(cs, ids);
        await AssertQueueEmptyAsync(service);
        // docs/testing.md: the duplicate metric is 1. The listener is process-wide, so only this test's service counts.
        metrics.Of("ledgerline.inbox.duplicates", m => Equals(m.Tag("ledgerline.service"), service)).Sum(m => (long)m.Value).ShouldBe(1);
    }

    [Fact]
    public async Task Same_message_delivered_concurrently_to_two_consumers_is_handled_once()
    {
        var (cs, ids) = await CreateDatabaseAsync(50);
        var service = "consumer" + Ids.New().ToString("N");
        using var first = CreateHost(cs, service);
        using var second = CreateHost(cs, service);
        await Task.WhenAll(first.StartAsync(TestContext.Current.CancellationToken), second.StartAsync(TestContext.Current.CancellationToken));
        await WaitConsumersAsync(service, 2);
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "consumer.event.#");
        await Task.WhenAll(ids.Select(async id =>
        {
            var messageId = Ids.New();
            await Task.WhenAll(probe.PublishAsync(new RenameWidget(id, "renamed"), id, messageId),
                probe.PublishAsync(new RenameWidget(id, "renamed"), id, messageId));
        }));
        await Eventually.ShouldAsync(async () => await InboxCountAsync(cs) == 50);
        await WaitQueueDrainedAsync(service);
        await Task.WhenAll(first.StopAsync(TestContext.Current.CancellationToken), second.StopAsync(TestContext.Current.CancellationToken));
        await AssertEffectsAsync(cs, ids);
        await AssertQueueEmptyAsync(service);
    }

    [Fact]
    public async Task Handler_state_change_inbox_row_and_outbox_row_commit_atomically()
    {
        var (cs, ids) = await CreateDatabaseAsync(1);
        var service = "consumer" + Ids.New().ToString("N");
        var behavior = new HandlerBehavior { Fail = true };
        using var host = CreateHost(cs, service, behavior);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await WaitConsumersAsync(service, 1);
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "consumer.event.#");
        await probe.PublishAsync(new RenameWidget(ids[0], "renamed"), ids[0]);
        await behavior.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await using var db = OpenDb(cs);
        (await db.Widgets.SingleAsync(TestContext.Current.CancellationToken)).Name.ShouldBe("original");
        (await db.Set<InboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.Set<OutboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    private IHost CreateHost(string cs, string service, HandlerBehavior? behavior = null, ushort prefetch = 16)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri }));
        builder.Services.AddSingleton(new ConsumerOptions { Service = service, Prefetch = prefetch });
        builder.Services.AddSingleton<MessageTypeRegistry>();
        builder.Services.AddSingleton(sp => new HandlerRegistry(sp.GetRequiredService<MessageTypeRegistry>()).Add<RenameWidget, RenameWidgetHandler>());
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<ICrashPoint, NoCrashPoint>();
        builder.Services.AddSingleton<IAckStrategy, DefaultAckStrategy>();
        builder.Services.AddSingleton(behavior ?? new HandlerBehavior());
        builder.Services.AddDbContext<TestDbContext>(options => options.UseNpgsql(cs));
        builder.Services.AddScoped<IOutbox, EfOutbox<TestDbContext>>();
        builder.Services.AddScoped<RenameWidgetHandler>();
        builder.Services.AddHostedService<ConsumerHost<TestDbContext>>();
        return builder.Build();
    }

    private async Task<(string ConnectionString, Guid[] Ids)> CreateDatabaseAsync(int count)
    {
        var cs = await infrastructure.CreateDatabaseAsync("consumer");
        await using var db = OpenDb(cs);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var ids = Enumerable.Range(0, count).Select(_ => Ids.New()).ToArray();
        db.Widgets.AddRange(ids.Select(id => new Widget(id, "original")));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (cs, ids);
    }

    private static TestDbContext OpenDb(string cs) => new(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(cs).Options);
    private static async Task<int> InboxCountAsync(string cs)
    {
        await using var db = OpenDb(cs);
        return await db.Set<InboxMessage>().CountAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AssertEffectsAsync(string cs, Guid[] ids)
    {
        await using var db = OpenDb(cs);
        var widgets = await db.Widgets.ToListAsync(TestContext.Current.CancellationToken);
        widgets.Count.ShouldBe(ids.Length);
        widgets.ShouldAllBe(w => w.Name == "renamed");
        (await db.Set<InboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(ids.Length);
        (await db.Set<OutboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(ids.Length);
    }

    private async Task WaitConsumersAsync(string service, uint count) => await Eventually.ShouldAsync(async () =>
    {
        await using var connection = new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri });
        await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        try { return (await channel.QueueDeclarePassiveAsync(Topology.InboxQueue(service), TestContext.Current.CancellationToken)).ConsumerCount == count; }
        catch (OperationInterruptedException) { return false; }
    });

    private async Task WaitQueueDrainedAsync(string service) => await Eventually.ShouldAsync(async () =>
    {
        await using var connection = new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri });
        await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        return (await channel.QueueDeclarePassiveAsync(Topology.InboxQueue(service), TestContext.Current.CancellationToken)).MessageCount == 0;
    });

    private async Task AssertQueueEmptyAsync(string service)
    {
        await using var connection = new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri });
        await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        (await channel.QueueDeclarePassiveAsync(Topology.InboxQueue(service), TestContext.Current.CancellationToken)).MessageCount.ShouldBe(0u);
    }

    public sealed class HandlerBehavior
    {
        public bool Fail { get; init; }
        public TaskCompletionSource Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class RenameWidgetHandler(TestDbContext db, IOutbox outbox, HandlerBehavior behavior) : IMessageHandler<RenameWidget>
    {
        public async Task HandleAsync(RenameWidget message, MessageContext context, CancellationToken ct)
        {
            var widget = await db.Widgets.SingleAsync(w => w.Id == message.Id, ct);
            db.Entry(widget).Property(w => w.Name).CurrentValue = message.Name;
            outbox.Enqueue(new WidgetRenamed(message.Id, message.Name), context.CorrelationId, context.MessageId);
            behavior.Invoked.TrySetResult();
            if (behavior.Fail) throw new InvalidOperationException("injected handler failure");
        }
    }

    [MessageType("consumer.command.rename-widget.v1")]
    public sealed record RenameWidget(Guid Id, string Name);
    [MessageType("consumer.event.widget-renamed.v1")]
    public sealed record WidgetRenamed(Guid Id, string Name);
}
