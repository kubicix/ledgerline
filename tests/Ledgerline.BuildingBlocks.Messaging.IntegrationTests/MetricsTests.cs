using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.BuildingBlocks.Messaging.IntegrationTests.Support;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

/// <summary>
/// The meter listener is process-wide, so every read filters on the per-service tag; each test uses a unique service name.
/// The class stops the shared broker in one test, so it runs in the non-parallel broker collection.
/// </summary>
[Collection("Broker lifecycle")]
public sealed class MetricsTests(InfrastructureFixture infrastructure)
{
    private const string ServiceTag = "ledgerline.service";

    [Fact]
    public async Task Outbox_lag_is_recorded_per_published_message()
    {
        using var metrics = MetricRecorder.ForMeter("Ledgerline");
        var cs = await CreateDatabaseAsync();
        var uri = await VirtualHostAsync(cs);
        var service = "lag" + Ids.New().ToString("N");
        await using var probe = await BrokerProbe.CreateAsync(uri, "metrics.event.#");
        using var host = await StartHostAsync(cs, uri, service);
        await EnqueueAsync(host, 3);

        await Eventually.ShouldAsync(() => Task.FromResult(Lag(metrics, service).Length == 3), because: "Expected one lag per message");
        await Eventually.ShouldAsync(() => Task.FromResult(probe.Messages.Count == 3));
        var lags = Lag(metrics, service);
        lags.ShouldAllBe(m => m.Unit == "s");
        lags.Select(m => (double)m.Value).ShouldAllBe(seconds => seconds >= 0 && seconds < 60);
        (await ProcessedAsync(cs)).ShouldBe(3);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Outbox_pending_gauge_rises_while_broker_is_down()
    {
        using var metrics = MetricRecorder.ForMeter("Ledgerline");
        var cs = await CreateDatabaseAsync();
        var service = "pending" + Ids.New().ToString("N");
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "metrics.event.#");
        using var host = await StartHostAsync(cs, infrastructure.RabbitMqUri, service);
        await Eventually.ShouldAsync(() => Task.FromResult(LatestPending(metrics, service) == 0), because: "Expected an empty backlog");
        await infrastructure.StopRabbitMqAsync();
        try
        {
            await EnqueueAsync(host, 5);
            await Eventually.ShouldAsync(() => Task.FromResult(LatestPending(metrics, service) == 5), because: "Expected the backlog to rise");
            (await ProcessedAsync(cs)).ShouldBe(0);
        }
        finally { await infrastructure.StartRabbitMqAsync(); }
        await Eventually.ShouldAsync(() => Task.FromResult(LatestPending(metrics, service) == 0), TimeSpan.FromSeconds(30),
            because: "Expected the backlog to drain after recovery");
        (await ProcessedAsync(cs)).ShouldBe(5);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Inbox_duplicate_increments_counter()
    {
        using var metrics = MetricRecorder.ForMeter("Ledgerline");
        var cs = await CreateDatabaseAsync();
        var uri = await VirtualHostAsync(cs);
        var service = "duplicate" + Ids.New().ToString("N");
        using var host = await StartHostAsync(cs, uri, service, consume: true);
        await using var probe = await BrokerProbe.CreateAsync(uri, "metrics.event.#");
        var messageId = Ids.New();
        await probe.PublishAsync(new Ping("once", 0), messageId, messageId);
        await probe.PublishAsync(new Ping("once", 0), messageId, messageId);

        await Eventually.ShouldAsync(() => Task.FromResult(Sum(metrics, "ledgerline.inbox.duplicates", service) == 1),
            because: "Expected the second delivery to count as a duplicate");
        await host.StopAsync(TestContext.Current.CancellationToken);
        Sum(metrics, "ledgerline.inbox.duplicates", service).ShouldBe(1);
        await using var db = OpenDb(cs);
        (await db.Widgets.SingleAsync(TestContext.Current.CancellationToken)).Name.ShouldBe("once");
        (await db.Set<InboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task Dead_lettered_message_increments_counter()
    {
        using var metrics = MetricRecorder.ForMeter("Ledgerline");
        var cs = await CreateDatabaseAsync();
        var uri = await VirtualHostAsync(cs);
        var service = "poison" + Ids.New().ToString("N");
        using var host = await StartHostAsync(cs, uri, service, consume: true);
        await using var probe = await BrokerProbe.CreateAsync(uri, "metrics.event.#");
        await probe.PublishAsync(new Ping("poison", int.MaxValue), Ids.New());

        await Eventually.ShouldAsync(() => Task.FromResult(Sum(metrics, "ledgerline.consumer.dead_lettered", service) == 1),
            because: "Expected the poison message to be dead-lettered");
        await host.StopAsync(TestContext.Current.CancellationToken);
        Sum(metrics, "ledgerline.consumer.retries", service).ShouldBe(2);
        Sum(metrics, "ledgerline.consumer.dead_lettered", service).ShouldBe(1);
        await using var db = OpenDb(cs);
        (await db.Set<InboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    private static RecordedMeasurement[] Lag(MetricRecorder metrics, string service) =>
        metrics.Of("ledgerline.outbox.lag", m => Equals(m.Tag(ServiceTag), service));

    private static long? LatestPending(MetricRecorder metrics, string service) =>
        metrics.Of("ledgerline.outbox.pending", m => Equals(m.Tag(ServiceTag), service)).LastOrDefault()?.Value as long?;

    private static long Sum(MetricRecorder metrics, string instrument, string service) =>
        metrics.Of(instrument, m => Equals(m.Tag(ServiceTag), service)).Sum(m => (long)m.Value);

    private async Task<string> VirtualHostAsync(string cs) =>
        await infrastructure.CreateRabbitMqUriAsync(new NpgsqlConnectionStringBuilder(cs).Database!);

    private static async Task<IHost> StartHostAsync(string cs, string rabbitUri, string service, bool consume = false)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:RabbitMq:Uri"] = rabbitUri,
            ["Messaging:Relay:PollIntervalMs"] = "20",
            ["Messaging:Consumer:RetryTtlMs"] = "100",
            ["Messaging:Consumer:MaxAttempts"] = "3"
        });
        builder.Services.AddDbContext<TestDbContext>(options => options.UseNpgsql(cs));
        builder.Services.AddLedgerlineMessaging<TestDbContext>(builder.Configuration, m =>
        {
            m.Service(service);
            if (consume) m.Consume<Ping, PingHandler>();
        });
        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task EnqueueAsync(IHost host, int count)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        foreach (var number in Enumerable.Range(1, count)) outbox.Enqueue(new Measured(number), Ids.New());
        await scope.ServiceProvider.GetRequiredService<TestDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var cs = await infrastructure.CreateDatabaseAsync("metrics");
        await using var db = OpenDb(cs);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return cs;
    }

    private static TestDbContext OpenDb(string cs) => new(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(cs).Options);

    private static async Task<int> ProcessedAsync(string cs)
    {
        await using var db = OpenDb(cs);
        return await db.Set<OutboxMessage>().CountAsync(x => x.ProcessedAt != null, TestContext.Current.CancellationToken);
    }

    public sealed class PingHandler(TestDbContext db) : IMessageHandler<Ping>
    {
        public Task HandleAsync(Ping message, MessageContext context, CancellationToken ct)
        {
            if (context.Attempt <= message.Failures) throw new InvalidOperationException("injected failure");
            db.Widgets.Add(new Widget(context.MessageId, message.Name));
            return Task.CompletedTask;
        }
    }

    [MessageType("metrics.command.ping.v1")]
    public sealed record Ping(string Name, int Failures);
    [MessageType("metrics.event.measured.v1")]
    public sealed record Measured(int Number);
}
