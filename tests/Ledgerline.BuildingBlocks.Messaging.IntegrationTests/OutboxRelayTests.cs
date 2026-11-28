using System.Collections.Concurrent;
using System.Data.Common;
using Ledgerline.BuildingBlocks.Messaging.IntegrationTests.Support;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

public sealed class OutboxRelayTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Relay_publishes_pending_rows_in_sequence_order_and_marks_them_processed()
    {
        var cs = await CreateDatabaseAsync();
        await SeedAsync(cs, 20);
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "relay.event.#");
        using var host = CreateHost(cs);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.ShouldAsync(async () => await ProcessedAsync(cs) == 20);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await BarrierAsync(probe);
        var messages = Recorded(probe);
        messages.Select(m => m.Payload.GetProperty("number").GetInt32()).ShouldBe(Enumerable.Range(1, 20));
        messages.Select(m => m.MessageId).Distinct().Count().ShouldBe(20);
    }

    [Fact]
    public async Task Relay_backs_off_and_records_error_when_publish_fails()
    {
        var cs = await CreateDatabaseAsync();
        await SeedAsync(cs, 1);
        var publisher = new ControlledPublisher((_, attempt) => attempt <= 3);
        using var host = CreateHost(cs, publisher);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.ShouldAsync(async () => await ProcessedAsync(cs) == 1);
        await using var db = OpenDb(cs);
        var row = await db.Set<OutboxMessage>().SingleAsync(TestContext.Current.CancellationToken);
        row.Attempts.ShouldBe(3);
        row.LastError.ShouldNotBeNullOrWhiteSpace();
        publisher.Successes.Single().ShouldBe(row.MessageId);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Relay_stops_batch_at_first_failure()
    {
        var cs = await CreateDatabaseAsync();
        await SeedAsync(cs, 5);
        var sawCommittedFailure = false;
        var publisher = new ControlledPublisher((message, attempt) => message.Sequence == 2 && attempt == 1);
        publisher.BeforePublish = async message =>
        {
            if (message.Sequence != 3) return;
            await using var db = OpenDb(cs);
            var first = await db.Set<OutboxMessage>().SingleAsync(x => x.Sequence == 1, TestContext.Current.CancellationToken);
            var second = await db.Set<OutboxMessage>().SingleAsync(x => x.Sequence == 2, TestContext.Current.CancellationToken);
            sawCommittedFailure = first.ProcessedAt is not null && second.Attempts == 1;
        };
        using var host = CreateHost(cs, publisher);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.ShouldAsync(async () => await ProcessedAsync(cs) == 5);
        await host.StopAsync(TestContext.Current.CancellationToken);
        sawCommittedFailure.ShouldBeTrue();
        publisher.Calls.Take(2).ShouldBe([1L, 2L]);
    }

    [Fact]
    public async Task Relay_does_not_publish_past_a_head_row_that_is_not_yet_due()
    {
        var cs = await CreateDatabaseAsync();
        await SeedAsync(cs, 5);
        await using (var db = OpenDb(cs))
        {
            // Due times come from the DB clock; the head is due later than DB now but earlier than the app clock.
            await db.Database.ExecuteSqlAsync($"""
                UPDATE outbox_messages SET next_attempt_at = CASE WHEN sequence = (SELECT min(sequence) FROM outbox_messages)
                    THEN now() + interval '30 minutes' ELSE now() - interval '1 minute' END
                """, TestContext.Current.CancellationToken);
        }
        var publisher = new ControlledPublisher((_, _) => false);
        var batches = new BatchObserver();
        using var host = CreateHost(cs, publisher, observer: batches, clock: new AheadClock());
        await host.StartAsync(TestContext.Current.CancellationToken);
        // A third relay transaction means at least two full batches have finished.
        await Eventually.ShouldAsync(() => Task.FromResult(batches.Started >= 3));
        publisher.Calls.ShouldBeEmpty();
        await using (var db = OpenDb(cs))
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE outbox_messages SET next_attempt_at = now() - interval '1 second'
                WHERE sequence = (SELECT min(sequence) FROM outbox_messages)
                """, TestContext.Current.CancellationToken);
        }
        await Eventually.ShouldAsync(async () => await ProcessedAsync(cs) == 5);
        await host.StopAsync(TestContext.Current.CancellationToken);
        var first = await FirstSequenceAsync(cs);
        publisher.Calls.ShouldBe(Enumerable.Range(0, 5).Select(i => first + i));
    }

    [Fact]
    public async Task Two_relays_do_not_publish_the_same_row_twice()
    {
        var cs = await CreateDatabaseAsync();
        await SeedAsync(cs, 200);
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "relay.event.#");
        using var first = CreateHost(cs);
        using var second = CreateHost(cs);
        await Task.WhenAll(first.StartAsync(TestContext.Current.CancellationToken), second.StartAsync(TestContext.Current.CancellationToken));
        await Eventually.ShouldAsync(async () => await ProcessedAsync(cs) == 200);
        await Task.WhenAll(first.StopAsync(TestContext.Current.CancellationToken), second.StopAsync(TestContext.Current.CancellationToken));
        await BarrierAsync(probe);
        var messages = Recorded(probe);
        messages.Length.ShouldBe(200);
        messages.Select(m => m.MessageId).Distinct().Count().ShouldBe(200);
    }

    [Fact]
    public async Task Relay_reacts_to_signal_faster_than_poll_interval()
    {
        var cs = await CreateDatabaseAsync();
        var observer = new ScanObserver();
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "relay.event.#");
        using var host = CreateHost(cs, pollMs: 10_000, observer: observer);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await observer.Scanned.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        new EfOutbox<TestDbContext>(db, new MessageTypeRegistry(), new SystemClock()).Enqueue(new RecordedMessage(1), Ids.New());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await probe.WaitForAsync<RecordedMessage>(m => m.Number == 1, TimeSpan.FromSeconds(1));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Signal_waits_for_explicit_transaction_commit()
    {
        var cs = await CreateDatabaseAsync();
        using var signal = new OutboxSignal();
        using var wakeUp = signal.Subscribe();
        var interceptor = new OutboxSaveChangesInterceptor(signal);
        await using var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(cs).AddInterceptors(interceptor).Options);
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        new EfOutbox<TestDbContext>(db, new MessageTypeRegistry(), new SystemClock()).Enqueue(new RecordedMessage(1), Ids.New());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await wakeUp.WaitAsync(TimeSpan.Zero, TestContext.Current.CancellationToken)).ShouldBeFalse();
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        (await wakeUp.WaitAsync(TimeSpan.Zero, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task Rolled_back_outbox_does_not_signal()
    {
        var cs = await CreateDatabaseAsync();
        using var signal = new OutboxSignal();
        using var wakeUp = signal.Subscribe();
        var interceptor = new OutboxSaveChangesInterceptor(signal);
        await using var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(cs).AddInterceptors(interceptor).Options);
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        new EfOutbox<TestDbContext>(db, new MessageTypeRegistry(), new SystemClock()).Enqueue(new RecordedMessage(1), Ids.New());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        (await wakeUp.WaitAsync(TimeSpan.Zero, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private IHost CreateHost(string cs, IOutboxPublisher? publisher = null, int pollMs = 10, IInterceptor? observer = null,
        IClock? clock = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new OutboxSignal());
        builder.Services.AddSingleton(new RelayOptions { PollIntervalMs = pollMs, BatchSize = 100 });
        builder.Services.AddSingleton(clock ?? new SystemClock());
        builder.Services.AddSingleton<ICrashPoint, NoCrashPoint>();
        builder.Services.AddScoped<OutboxSaveChangesInterceptor>();
        builder.Services.AddDbContext<TestDbContext>((sp, options) =>
        {
            options.UseNpgsql(cs).AddInterceptors(sp.GetRequiredService<OutboxSaveChangesInterceptor>());
            if (observer is not null) options.AddInterceptors(observer);
        });
        builder.Services.AddSingleton(new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri }));
        if (publisher is null) builder.Services.AddSingleton<IOutboxPublisher, RabbitMqOutboxPublisher>();
        else builder.Services.AddSingleton(publisher);
        builder.Services.AddHostedService<OutboxRelay<TestDbContext>>();
        return builder.Build();
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var cs = await infrastructure.CreateDatabaseAsync("relay");
        await using var db = OpenDb(cs);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return cs;
    }

    private static TestDbContext OpenDb(string cs) => new(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(cs).Options);

    private static async Task SeedAsync(string cs, int count)
    {
        await using var db = OpenDb(cs);
        var outbox = new EfOutbox<TestDbContext>(db, new MessageTypeRegistry(), new SystemClock());
        foreach (var number in Enumerable.Range(1, count)) outbox.Enqueue(new RecordedMessage(number), Ids.New());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> ProcessedAsync(string cs)
    {
        await using var db = OpenDb(cs);
        return await db.Set<OutboxMessage>().CountAsync(x => x.ProcessedAt != null, TestContext.Current.CancellationToken);
    }

    private static async Task<long> FirstSequenceAsync(string cs)
    {
        await using var db = OpenDb(cs);
        return await db.Set<OutboxMessage>().MinAsync(x => x.Sequence, TestContext.Current.CancellationToken);
    }

    private static MessageEnvelope[] Recorded(BrokerProbe probe) => probe.Messages.Where(m => m.Type == "relay.event.recorded.v1").ToArray();

    private static async Task BarrierAsync(BrokerProbe probe)
    {
        var id = Ids.New();
        await probe.PublishAsync(new Barrier(id), id);
        await probe.WaitForAsync<Barrier>(m => m.Id == id);
    }

    private sealed class ControlledPublisher(Func<OutboxMessage, int, bool> fails) : IOutboxPublisher
    {
        private readonly ConcurrentDictionary<Guid, int> attempts = new();
        public string Transport => RabbitMqOutboxPublisher.TransportName;
        public ConcurrentQueue<long> Calls { get; } = new();
        public ConcurrentQueue<Guid> Successes { get; } = new();
        public Func<OutboxMessage, Task>? BeforePublish { get; set; }
        public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Enqueue(message.Sequence);
            if (BeforePublish is not null) await BeforePublish(message);
            if (fails(message, attempts.AddOrUpdate(message.MessageId, 1, (_, value) => value + 1)))
                throw new PublishFailedException("injected failure");
            Successes.Enqueue(message.MessageId);
        }
    }

    private sealed class ScanObserver : DbCommandInterceptor
    {
        public TaskCompletionSource Scanned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SKIP LOCKED", StringComparison.Ordinal)) Scanned.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BatchObserver : DbTransactionInterceptor
    {
        private int started;
        public int Started => Volatile.Read(ref started);
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref started);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AheadClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddHours(1);
    }

    [MessageType("relay.event.recorded.v1")]
    public sealed record RecordedMessage(int Number);
    [MessageType("relay.event.barrier.v1")]
    public sealed record Barrier(Guid Id);
}
