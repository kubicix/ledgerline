using System.Net;
using System.Net.Sockets;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;

namespace Ledgerline.Accounts.IntegrationTests;

[Collection("Dependency lifecycle")]
public sealed class HealthTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Ready_is_503_when_database_is_unreachable()
    {
        await using var host = await StartWithUnreachableDatabaseAsync("health_db");
        using var client = host.CreateClient();
        (await StatusAsync(client, "/health/ready")).ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await StatusAsync(client, "/health/live")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_is_503_while_broker_is_down_and_recovers()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "health_broker");
        using var client = host.CreateClient();
        (await StatusAsync(client, "/health/ready")).ShouldBe(HttpStatusCode.OK);
        await infrastructure.StopRabbitMqAsync();
        try
        {
            await Eventually.ShouldAsync(async () => await StatusAsync(client, "/health/ready") == HttpStatusCode.ServiceUnavailable);
        }
        finally { await infrastructure.StartRabbitMqAsync(); }
        await Eventually.ShouldAsync(async () => await StatusAsync(client, "/health/ready") == HttpStatusCode.OK, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Live_stays_200_while_dependencies_are_down()
    {
        await using var host = await StartWithUnreachableDatabaseAsync("health_live");
        using var client = host.CreateClient();
        await infrastructure.StopRabbitMqAsync();
        try
        {
            await Eventually.ShouldAsync(async () => await StatusAsync(client, "/health/ready") == HttpStatusCode.ServiceUnavailable);
            (await StatusAsync(client, "/health/live")).ShouldBe(HttpStatusCode.OK);
        }
        finally { await infrastructure.StartRabbitMqAsync(); }
        (await StatusAsync(client, "/health/live")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_turns_503_when_shutdown_starts()
    {
        var gate = new StopGate();
        var host = await ServiceHost<Program>.StartAsync(infrastructure, "health_stop", services => services.AddHostedService(_ => gate));
        try
        {
            using var client = host.CreateClient();
            (await StatusAsync(client, "/health/ready")).ShouldBe(HttpStatusCode.OK);
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.StopApplication();
            await gate.Stopping.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            (await StatusAsync(client, "/health/ready")).ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await StatusAsync(client, "/health/live")).ShouldBe(HttpStatusCode.OK);
            gate.Release();
            await Eventually.ShouldAsync(() => Task.FromResult(lifetime.ApplicationStopped.IsCancellationRequested), TimeSpan.FromSeconds(30));
        }
        finally
        {
            gate.Release();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task In_flight_message_completes_during_shutdown()
    {
        var acknowledgments = new LedgerCommandTests.CountingAck();
        var gate = new LedgerGate();
        var slowStopBeforeConsumer = new StopGate();
        var consumerStopped = new StopProbe(gate);
        var host = await ServiceHost<Program>.StartAsync(infrastructure, "health_drain", services =>
        {
            services.AddSingleton<IAckStrategy>(acknowledgments);
            services.AddScoped<Ledgerline.Accounts.Ledger.Ledger>();
            services.AddScoped<ILedger>(provider => new GatedLedger(provider.GetRequiredService<Ledgerline.Accounts.Ledger.Ledger>(), gate));
            // Hosted services stop in reverse registration order. The gate registered after the consumer host stops just
            // before it and holds, like a slow web server stop; the probe registered before it stops once it has returned.
            var consumer = services.IndexOf(services.Single(x => x.ServiceType == typeof(IHostedService)
                && x.ImplementationType == typeof(ConsumerHost<AccountsDbContext>)));
            services.Insert(consumer + 1, ServiceDescriptor.Singleton<IHostedService>(slowStopBeforeConsumer));
            services.Insert(consumer, ServiceDescriptor.Singleton<IHostedService>(consumerStopped));
        });
        var cs = host.ConnectionString;
        var rabbitUri = host.Services.GetRequiredService<RabbitMqOptions>().Uri;
        var operation = Ids.New();
        var messageId = Ids.New();
        try
        {
            // The consumer drain (20 s) must fit inside the host shutdown budget.
            host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout.ShouldBe(TimeSpan.FromSeconds(25));
            Guid wallet;
            await using (var scope = host.Services.CreateAsyncScope())
                wallet = await TestAccounts.OpenWalletAsync(scope.ServiceProvider.GetRequiredService<AccountsDbContext>(), Ids.New(), "TRY");
            await using var probe = await BrokerProbe.CreateAsync(rabbitUri, "accounts.event.#");
            var payment = Ids.New();
            await probe.PublishAsync(new CreditTopUp(operation, payment, wallet, 1000, "TRY"), payment, messageId);
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var consumerHost = host.Services.GetServices<IHostedService>().OfType<ConsumerHost<AccountsDbContext>>().Single();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.StopApplication();
            // The host is held before ConsumerHost.StopAsync is called; the handler is still in flight: not ready, still live.
            await slowStopBeforeConsumer.Stopping.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            using (var client = host.CreateClient())
            {
                (await StatusAsync(client, "/health/ready")).ShouldBe(HttpStatusCode.ServiceUnavailable);
                (await StatusAsync(client, "/health/live")).ShouldBe(HttpStatusCode.OK);
            }
            consumerHost.ExecuteTask!.IsCompleted.ShouldBeFalse();
            gate.Release();
            // The drain started at ApplicationStopping: it finishes, with the delivery acknowledged, while the host is
            // still held before ConsumerHost.StopAsync.
            await Eventually.ShouldAsync(() => Task.FromResult(consumerHost.ExecuteTask.IsCompleted));
            acknowledgments.Count.ShouldBe(1);
            consumerStopped.Stopped.IsCompleted.ShouldBeFalse();
            slowStopBeforeConsumer.Release();
            await consumerStopped.Stopped.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            consumerStopped.StoppedWhileHandlerBlocked.ShouldBeFalse();
            await Eventually.ShouldAsync(() => Task.FromResult(lifetime.ApplicationStopped.IsCancellationRequested), TimeSpan.FromSeconds(30));
            acknowledgments.Count.ShouldBe(1);
        }
        finally
        {
            gate.Release();
            slowStopBeforeConsumer.Release();
            await host.DisposeAsync();
        }
        // The host and its broker connection are gone: an unacknowledged delivery would be back in the queue now.
        (await InboxReadyMessagesAsync(rabbitUri)).ShouldBe(0u);
        await using var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(cs).Options);
        (await db.Set<InboxMessage>().CountAsync(x => x.MessageId == messageId, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.JournalEntries.CountAsync(x => x.OperationId == operation, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.LedgerOperations.SingleAsync(x => x.OperationId == operation, TestContext.Current.CancellationToken)).Outcome.ShouldBe("Succeeded");
        await LedgerAssert.InvariantsHoldAsync(cs);
    }

    private async Task<ServiceHost<Program>> StartWithUnreachableDatabaseAsync(string prefix)
    {
        var wrongPort = new NpgsqlConnectionStringBuilder(infrastructure.PostgresAdminConnectionString)
        {
            Host = "127.0.0.1",
            Port = UnusedPort(),
            Database = "unavailable",
            Timeout = 1
        }.ConnectionString;
        return await ServiceHost<Program>.StartAsync(infrastructure, prefix, services =>
            services.AddDbContext<AccountsDbContext>(options => options.UseNpgsql(wrongPort)));
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static async Task<uint> InboxReadyMessagesAsync(string rabbitUri)
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(rabbitUri) }.CreateConnectionAsync(TestContext.Current.CancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (await channel.QueueDeclarePassiveAsync(Topology.InboxQueue("accounts"), TestContext.Current.CancellationToken)).MessageCount;
    }

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Holds the host at this point of its reverse-order stop until released.</summary>
    private sealed class StopGate : IHostedService
    {
        private readonly TaskCompletionSource stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Stopping => stopping.Task;
        public void Release() => released.TrySetResult();
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            stopping.TrySetResult();
            await released.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Records when the host reaches this point of its reverse-order stop, and whether the gated handler was still blocked.</summary>
    private sealed class StopProbe(LedgerGate gate) : IHostedService
    {
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Stopped => stopped.Task;
        public bool StoppedWhileHandlerBlocked { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StoppedWhileHandlerBlocked = !gate.IsReleased;
            stopped.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class LedgerGate
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => entered.Task;
        public bool IsReleased => released.Task.IsCompleted;
        public void Release() => released.TrySetResult();

        public Task WaitAsync()
        {
            entered.TrySetResult();
            return released.Task;
        }
    }

    /// <summary>Blocks the posting inside the consumer transaction, after the inbox row is inserted and before commit.</summary>
    private sealed class GatedLedger(ILedger inner, LedgerGate gate) : ILedger
    {
        public async Task<PostingResult> PostAsync(PostingRequest request, CancellationToken ct)
        {
            await gate.WaitAsync();
            return await inner.PostAsync(request, ct);
        }
    }
}
