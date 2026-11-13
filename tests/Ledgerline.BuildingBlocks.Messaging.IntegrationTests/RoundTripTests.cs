using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.BuildingBlocks.Messaging.IntegrationTests.Support;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

public sealed class RoundTripTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Ping_from_one_service_produces_pong_from_the_other()
    {
        var pingDb = await CreateDatabaseAsync();
        var pongDb = await CreateDatabaseAsync();
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "roundtrip.command.#", "roundtrip.event.#");
        using var ping = CreateHost(pingDb, "ping" + Ids.New().ToString("N"), m => m.Consume<Pong, PongHandler>());
        using var pong = CreateHost(pongDb, "pong" + Ids.New().ToString("N"), m => m.Consume<Ping, PingHandler>());
        await ping.StartAsync(TestContext.Current.CancellationToken);
        await pong.StartAsync(TestContext.Current.CancellationToken);
        var correlation = Ids.New();
        await EnqueueAsync(ping, new Ping("roundtrip"), correlation);
        await Eventually.ShouldAsync(async () =>
        {
            await using var db = OpenDb(pingDb);
            return await db.Widgets.AnyAsync(w => w.Name == "roundtrip", TestContext.Current.CancellationToken);
        });
        await probe.WaitForAsync<Pong>(p => p.Name == "roundtrip");
        var sent = probe.Messages.Single(m => m.Type == "roundtrip.command.ping.v1");
        var reply = probe.Messages.Single(m => m.Type == "roundtrip.event.pong.v1");
        reply.CorrelationId.ShouldBe(correlation);
        reply.CausationId.ShouldBe(sent.MessageId);
        await pong.StopAsync(TestContext.Current.CancellationToken);
        await ping.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Host_stops_gracefully_with_in_flight_handler()
    {
        var cs = await CreateDatabaseAsync();
        var service = "graceful" + Ids.New().ToString("N");
        var state = new HandlerGate { Block = true };
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "roundtrip.event.#");
        using var host = CreateHost(cs, service, m => m.Consume<Ping, PingHandler>(), state);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await probe.PublishAsync(new Ping("in-flight"), Ids.New());
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var stopping = host.StopAsync(TestContext.Current.CancellationToken);
        stopping.IsCompleted.ShouldBeFalse();
        state.Release.TrySetResult();
        await stopping;
        await using var db = OpenDb(cs);
        (await db.Widgets.SingleAsync(TestContext.Current.CancellationToken)).Name.ShouldBe("in-flight");
        (await db.Set<InboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        await using var connection = new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri });
        await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        (await channel.QueueDeclarePassiveAsync(Topology.InboxQueue(service), TestContext.Current.CancellationToken)).MessageCount.ShouldBe(0u);
    }

    [Fact]
    public async Task Retention_job_deletes_only_old_processed_rows()
    {
        var cs = await CreateDatabaseAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        var expired = OutboxRow(old, old);
        var pending = OutboxRow(old, null);
        var recent = OutboxRow(old, DateTimeOffset.UtcNow);
        var oldInbox = new InboxMessage { MessageId = Ids.New(), Consumer = "old", Type = "test", ReceivedAt = old };
        var recentInbox = new InboxMessage { MessageId = Ids.New(), Consumer = "new", Type = "test", ReceivedAt = DateTimeOffset.UtcNow };
        await SeedRetentionAsync(cs, [expired, pending, recent], [oldInbox, recentInbox]);
        using var host = CreateHost(cs, "retention" + Ids.New().ToString("N"), _ => { });
        var job = host.Services.GetServices<IHostedService>().OfType<MessagingRetentionJob<TestDbContext>>().Single();
        await job.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.ShouldAsync(async () =>
        {
            await using var db = OpenDb(cs);
            return await db.Set<OutboxMessage>().CountAsync(TestContext.Current.CancellationToken) == 2
                && await db.Set<InboxMessage>().CountAsync(TestContext.Current.CancellationToken) == 1;
        });
        await job.StopAsync(TestContext.Current.CancellationToken);
        await using var actual = OpenDb(cs);
        (await actual.Set<OutboxMessage>().Select(x => x.MessageId).ToListAsync(TestContext.Current.CancellationToken)).Order()
            .ShouldBe(new[] { pending.MessageId, recent.MessageId }.Order());
        (await actual.Set<InboxMessage>().SingleAsync(TestContext.Current.CancellationToken)).MessageId.ShouldBe(recentInbox.MessageId);
    }

    private IHost CreateHost(string cs, string service, Action<MessagingBuilder> configure, HandlerGate? gate = null)
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:RabbitMq:Uri"] = infrastructure.RabbitMqUri,
            ["Messaging:Relay:PollIntervalMs"] = "20",
            ["Messaging:Retention:Days"] = "7"
        }).Build();
        builder.Services.AddSingleton(gate ?? new HandlerGate());
        builder.Services.AddDbContext<TestDbContext>(options => options.UseNpgsql(cs));
        builder.Services.AddLedgerlineMessaging<TestDbContext>(configuration, m => { m.Service(service); configure(m); });
        return builder.Build();
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var cs = await infrastructure.CreateDatabaseAsync("roundtrip");
        await using var db = OpenDb(cs);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return cs;
    }

    private static TestDbContext OpenDb(string cs) => new(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(cs).Options);

    private static async Task EnqueueAsync<T>(IHost host, T payload, Guid correlation)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IOutbox>().Enqueue(payload, correlation);
        await scope.ServiceProvider.GetRequiredService<TestDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static OutboxMessage OutboxRow(DateTimeOffset occurred, DateTimeOffset? processed) => new()
    {
        MessageId = Ids.New(), Type = "roundtrip.event.pong.v1", Payload = EnvelopeSerializer.ToPayload(new Pong("retention")),
        CorrelationId = Ids.New(), OccurredAt = occurred, ProcessedAt = processed
    };

    private static async Task SeedRetentionAsync(string cs, OutboxMessage[] outbox, InboxMessage[] inbox)
    {
        await using var db = OpenDb(cs);
        db.AddRange(outbox);
        db.AddRange(inbox);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public sealed class HandlerGate
    {
        public bool Block { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class PingHandler(TestDbContext db, IOutbox outbox, HandlerGate gate) : IMessageHandler<Ping>
    {
        public async Task HandleAsync(Ping message, MessageContext context, CancellationToken ct)
        {
            gate.Started.TrySetResult();
            if (gate.Block) await gate.Release.Task.WaitAsync(ct);
            db.Widgets.Add(new Widget(context.MessageId, message.Name));
            outbox.Enqueue(new Pong(message.Name), context.CorrelationId, context.MessageId);
        }
    }

    public sealed class PongHandler(TestDbContext db) : IMessageHandler<Pong>
    {
        public Task HandleAsync(Pong message, MessageContext context, CancellationToken ct)
        {
            db.Widgets.Add(new Widget(context.MessageId, message.Name));
            return Task.CompletedTask;
        }
    }

    [MessageType("roundtrip.command.ping.v1")]
    public sealed record Ping(string Name);
    [MessageType("roundtrip.event.pong.v1")]
    public sealed record Pong(string Name);
}
