using System.Diagnostics.Metrics;
using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.BuildingBlocks.Messaging.IntegrationTests.Support;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.Testing;
using Ledgerline.Testing.Faults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

[Collection("Broker lifecycle")]
public sealed class FailureScenarioTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Outbox_keeps_messages_while_broker_is_down_and_delivers_after_recovery()
    {
        var cs = await CreateDatabaseAsync();
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "failure.event.#");
        using var host = CreateHost(cs, "outage" + Ids.New().ToString("N"));
        await host.StartAsync(TestContext.Current.CancellationToken);
        await infrastructure.StopRabbitMqAsync();
        try
        {
            await EnqueueAsync(host, 20);
            await Eventually.ShouldAsync(async () =>
            {
                await using var db = OpenDb(cs);
                return await db.Set<OutboxMessage>().AnyAsync(x => x.Attempts > 0, TestContext.Current.CancellationToken);
            });
            await using var pending = OpenDb(cs);
            (await pending.Set<OutboxMessage>().CountAsync(x => x.ProcessedAt == null, TestContext.Current.CancellationToken)).ShouldBe(20);
            host.Services.GetServices<IHostedService>().OfType<OutboxRelay<TestDbContext>>().Single().ExecuteTask!.IsCompleted.ShouldBeFalse();
        }
        finally { await infrastructure.StartRabbitMqAsync(); }
        await Eventually.ShouldAsync(async () => await ProcessedAsync(cs) == 20, TimeSpan.FromSeconds(30));
        await Eventually.ShouldAsync(() => Task.FromResult(probe.Messages.Count == 20));
        await host.StopAsync(TestContext.Current.CancellationToken);
        probe.Messages.Select(x => x.MessageId).Distinct().Count().ShouldBe(20);
        probe.Messages.Select(x => x.Payload.GetProperty("number").GetInt32()).ShouldBe(Enumerable.Range(1, 20));
    }

    [Fact]
    public async Task Crash_after_publish_before_marking_processed_republishes_and_consumer_deduplicates()
    {
        var sourceDb = await CreateDatabaseAsync();
        var destinationDb = await CreateDatabaseAsync();
        using var duplicates = new DuplicateMeasurements();
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "failure.event.#");
        using var destination = CreateHost(destinationDb, "destination" + Ids.New().ToString("N"), consume: true);
        await destination.StartAsync(TestContext.Current.CancellationToken);
        using (var crashed = CreateHost(sourceDb, "crashed" + Ids.New().ToString("N"), crash: true))
        {
            await crashed.StartAsync(TestContext.Current.CancellationToken);
            await EnqueueAsync(crashed, 1);
            var fault = (StoppingCrashPoint)crashed.Services.GetRequiredService<ICrashPoint>();
            await fault.Crashed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await crashed.Services.GetServices<IHostedService>().OfType<OutboxRelay<TestDbContext>>().Single()
                .ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await crashed.StopAsync(TestContext.Current.CancellationToken);
        }
        await using (var db = OpenDb(sourceDb))
            (await db.Set<OutboxMessage>().SingleAsync(TestContext.Current.CancellationToken)).ProcessedAt.ShouldBeNull();
        using var restarted = CreateHost(sourceDb, "restarted" + Ids.New().ToString("N"));
        await restarted.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.ShouldAsync(() => Task.FromResult(probe.Messages.Count == 2 && duplicates.Count == 1));
        await Eventually.ShouldAsync(async () => await ProcessedAsync(sourceDb) == 1);
        await restarted.StopAsync(TestContext.Current.CancellationToken);
        await destination.StopAsync(TestContext.Current.CancellationToken);
        probe.Messages.Select(x => x.MessageId).Distinct().Count().ShouldBe(1);
        await AssertOneEffectAsync(destinationDb);
    }

    [Fact]
    public async Task Crash_after_commit_before_ack_redelivers_and_inbox_absorbs_it()
    {
        var sourceDb = await CreateDatabaseAsync();
        var destinationDb = await CreateDatabaseAsync();
        using var duplicates = new DuplicateMeasurements();
        using var destination = CreateHost(destinationDb, "lostack" + Ids.New().ToString("N"), consume: true, dropAck: true);
        await destination.StartAsync(TestContext.Current.CancellationToken);
        using var source = CreateHost(sourceDb, "source" + Ids.New().ToString("N"));
        await source.StartAsync(TestContext.Current.CancellationToken);
        await EnqueueAsync(source, 1);
        await Eventually.ShouldAsync(() => Task.FromResult(duplicates.Count == 1));
        await ((DroppingAckStrategy)destination.Services.GetRequiredService<IAckStrategy>()).Closed;
        await source.StopAsync(TestContext.Current.CancellationToken);
        await destination.StopAsync(TestContext.Current.CancellationToken);
        await AssertOneEffectAsync(destinationDb);
    }

    private IHost CreateHost(string cs, string service, bool consume = false, bool crash = false, bool dropAck = false)
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:RabbitMq:Uri"] = infrastructure.RabbitMqUri,
            ["Messaging:Relay:PollIntervalMs"] = "20"
        }).Build();
        builder.Services.AddDbContext<TestDbContext>(options => options.UseNpgsql(cs));
        builder.Services.AddLedgerlineMessaging<TestDbContext>(configuration, m =>
        {
            m.Service(service);
            if (consume) m.Consume<Numbered, NumberedHandler>();
        });
        if (crash) builder.Services.AddSingleton<ICrashPoint>(sp => new StoppingCrashPoint("relay.after-publish", sp.GetRequiredService<IHostApplicationLifetime>()));
        if (dropAck) builder.Services.AddSingleton<IAckStrategy, DroppingAckStrategy>();
        return builder.Build();
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var cs = await infrastructure.CreateDatabaseAsync("failure");
        await using var db = OpenDb(cs);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return cs;
    }

    private static TestDbContext OpenDb(string cs) => new(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(cs).Options);

    private static async Task EnqueueAsync(IHost host, int count)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        foreach (var number in Enumerable.Range(1, count))
        {
            var id = Ids.New();
            db.Widgets.Add(new Widget(id, number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            outbox.Enqueue(new Numbered(number), id);
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> ProcessedAsync(string cs)
    {
        await using var db = OpenDb(cs);
        return await db.Set<OutboxMessage>().CountAsync(x => x.ProcessedAt != null, TestContext.Current.CancellationToken);
    }

    private static async Task AssertOneEffectAsync(string cs)
    {
        await using var db = OpenDb(cs);
        (await db.Widgets.SingleAsync(TestContext.Current.CancellationToken)).Name.ShouldBe("1");
        (await db.Set<InboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    private sealed class DuplicateMeasurements : IDisposable
    {
        private readonly MeterListener listener = new();
        private long count;
        public long Count => Interlocked.Read(ref count);

        public DuplicateMeasurements()
        {
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == "ledgerline.inbox.duplicates") meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref count, value));
            listener.Start();
        }
        public void Dispose() => listener.Dispose();
    }

    public sealed class NumberedHandler(TestDbContext db) : IMessageHandler<Numbered>
    {
        public Task HandleAsync(Numbered message, MessageContext context, CancellationToken ct)
        {
            db.Widgets.Add(new Widget(context.MessageId, message.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return Task.CompletedTask;
        }
    }

    [MessageType("failure.event.numbered.v1")]
    public sealed record Numbered(int Number);
}
