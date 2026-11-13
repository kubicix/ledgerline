using System.Diagnostics;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ledgerline.Accounts.IntegrationTests;

public sealed class ConcurrencyTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Concurrent_debits_never_overdraw()
    {
        await using var host = await StartAsync(8);
        var wallets = await OpenWalletsAsync(host, 51);
        await using var probe = await ProbeAsync(host);
        var seed = Ids.New();
        await probe.PublishAsync(new CreditTopUp(seed, Ids.New(), wallets[0], 10_000, "TRY"), seed);
        await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == seed);
        var commands = wallets.Skip(1).Select(id => new TransferFunds(Ids.New(), Ids.New(), wallets[0], id, 500, "TRY")).ToArray();
        await Task.WhenAll(commands.Select(c => probe.PublishAsync(c, c.PaymentId)));
        var replies = await WaitRepliesAsync(probe, commands.Select(c => c.OperationId).ToHashSet());
        replies.Count(x => x.Type == "accounts.event.ledger-operation-succeeded.v1").ShouldBe(20);
        replies.Count(x => x.Type == "accounts.event.ledger-operation-rejected.v1").ShouldBe(30);
        replies.Where(x => x.Type == "accounts.event.ledger-operation-rejected.v1")
            .ShouldAllBe(x => x.Payload.GetProperty("reasonCode").GetString() == "InsufficientFunds");
        await using var db = OpenDb(host.ConnectionString);
        (await db.Accounts.SingleAsync(x => x.Id == wallets[0], TestContext.Current.CancellationToken)).BalanceMinor.ShouldBe(0);
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    [Fact]
    public async Task Opposite_direction_transfers_do_not_deadlock()
    {
        using var errors = new DeadlockObserver();
        await using var first = await StartAsync(4, errors);
        await using var second = await ServiceHost<Program>.StartOnDatabaseAsync(infrastructure, first.ConnectionString,
            services => ConfigureConsumers(services, 4, errors));
        await WaitConsumersAsync(first, 8);
        var wallets = await OpenWalletsAsync(first, 2);
        await using var probe = await ProbeAsync(first);
        foreach (var wallet in wallets)
        {
            var op = Ids.New();
            await probe.PublishAsync(new CreditTopUp(op, Ids.New(), wallet, 10_000, "TRY"), op);
            await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == op);
        }
        var commands = Enumerable.Range(0, 400).Select(i => new TransferFunds(Ids.New(), Ids.New(), wallets[i % 2], wallets[1 - i % 2], 1, "TRY")).ToArray();
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(commands.Select(c => probe.PublishAsync(c, c.PaymentId)));
        var replies = await WaitRepliesAsync(probe, commands.Select(c => c.OperationId).ToHashSet());
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        replies.ShouldAllBe(x => x.Type == "accounts.event.ledger-operation-succeeded.v1");
        errors.Count.ShouldBe(0);
        await using var db = OpenDb(first.ConnectionString);
        (await db.Accounts.Where(x => wallets.Contains(x.Id)).Select(x => x.BalanceMinor).ToListAsync(TestContext.Current.CancellationToken)).ShouldAllBe(x => x == 10_000);
        await LedgerAssert.InvariantsHoldAsync(first.ConnectionString);
    }

    [Fact]
    public async Task Concurrent_postings_keep_stream_versions_gapless()
    {
        await using var host = await StartAsync(8);
        var wallets = await OpenWalletsAsync(host, 10);
        await using var probe = await ProbeAsync(host);
        foreach (var wallet in wallets)
        {
            var op = Ids.New();
            await probe.PublishAsync(new CreditTopUp(op, Ids.New(), wallet, 10_000, "TRY"), op);
            await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == op);
        }
        var operations = new HashSet<Guid>();
        for (var i = 0; i < 500; i++)
        {
            var op = Ids.New();
            var payment = Ids.New();
            operations.Add(op);
            if (i % 2 == 0) await probe.PublishAsync(new CreditTopUp(op, payment, wallets[i % 10], 1, "TRY"), payment);
            else await probe.PublishAsync(new TransferFunds(op, payment, wallets[i % 10], wallets[(i + 1) % 10], 1, "TRY"), payment);
        }
        (await WaitRepliesAsync(probe, operations)).ShouldAllBe(x => x.Type == "accounts.event.ledger-operation-succeeded.v1");
        await using var db = OpenDb(host.ConnectionString);
        var versions = await db.Events.Select(x => new { x.StreamId, x.Version }).ToListAsync(TestContext.Current.CancellationToken);
        foreach (var stream in versions.GroupBy(x => x.StreamId)) stream.Select(x => x.Version).Order().ShouldBe(Enumerable.Range(1, stream.Count()));
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    private async Task<ServiceHost<Program>> StartAsync(int consumers, DeadlockObserver? errors = null)
    {
        var host = await ServiceHost<Program>.StartAsync(infrastructure, "concurrency", services => ConfigureConsumers(services, consumers, errors));
        await WaitConsumersAsync(host, (uint)consumers);
        return host;
    }

    private static void ConfigureConsumers(IServiceCollection services, int consumers, DeadlockObserver? errors)
    {
        for (var i = 1; i < consumers; i++) services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<ConsumerHost<AccountsDbContext>>(sp));
        if (errors is not null) services.AddLogging(logging => logging.AddProvider(errors));
    }

    private static AccountsDbContext OpenDb(string cs) => new(new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(cs).Options);
    private static Task<BrokerProbe> ProbeAsync(ServiceHost<Program> host) =>
        BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.#");

    private static async Task<Guid[]> OpenWalletsAsync(ServiceHost<Program> host, int count)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++) ids.Add(await TestAccounts.OpenWalletAsync(db, Ids.New(), "TRY"));
        return ids.ToArray();
    }

    private static async Task WaitConsumersAsync(ServiceHost<Program> host, uint count)
    {
        await using var connection = new RabbitMqConnection(host.Services.GetRequiredService<RabbitMqOptions>());
        await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        await Eventually.ShouldAsync(async () => (await channel.QueueDeclarePassiveAsync("accounts.inbox", TestContext.Current.CancellationToken)).ConsumerCount == count);
    }

    private static async Task<MessageEnvelope[]> WaitRepliesAsync(BrokerProbe probe, HashSet<Guid> operations)
    {
        MessageEnvelope[] replies = [];
        await Eventually.ShouldAsync(() =>
        {
            replies = probe.Messages.Where(x => x.Type is "accounts.event.ledger-operation-succeeded.v1" or "accounts.event.ledger-operation-rejected.v1")
                .Where(x => operations.Contains(x.Payload.GetProperty("operationId").GetGuid()))
                .GroupBy(x => x.Payload.GetProperty("operationId").GetGuid()).Select(x => x.First()).ToArray();
            return Task.FromResult(replies.Length == operations.Count);
        }, TimeSpan.FromSeconds(30));
        return replies;
    }

    private sealed class DeadlockObserver : ILoggerProvider, ILogger
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            for (var error = exception; error is not null; error = error.InnerException)
                if (error is PostgresException { SqlState: "40P01" }) Interlocked.Increment(ref count);
        }
    }
}
