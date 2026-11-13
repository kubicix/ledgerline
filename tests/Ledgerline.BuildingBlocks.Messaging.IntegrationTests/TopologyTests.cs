using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Testing;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

public sealed class TopologyTests(InfrastructureFixture infrastructure)
{
    private const string TypeName = "accounts.command.reserve-funds.v1";

    [Fact]
    public async Task Declare_creates_exchange_three_queues_and_bindings()
    {
        var service = "topo" + Guid.NewGuid().ToString("N");
        await using var connection = CreateConnection();
        await using var channel = await connection.CreateChannelAsync(true, TestContext.Current.CancellationToken);
        await Topology.DeclareAsync(channel, service, [TypeName], TestContext.Current.CancellationToken);
        await channel.ExchangeDeclarePassiveAsync("ledgerline." + service, TestContext.Current.CancellationToken);
        await channel.ExchangeDeclarePassiveAsync("ledgerline.accounts", TestContext.Current.CancellationToken);
        foreach (var queue in Queues(service))
            await channel.QueueDeclarePassiveAsync(queue, TestContext.Current.CancellationToken);
        await channel.BasicPublishAsync("ledgerline.accounts", TypeName, "bound"u8.ToArray(), TestContext.Current.CancellationToken);
        BasicGetResult? received = null;
        await Eventually.ShouldAsync(async () => (received = await channel.BasicGetAsync(Topology.InboxQueue(service), true, TestContext.Current.CancellationToken)) is not null);
        System.Text.Encoding.UTF8.GetString(received!.Body.Span).ShouldBe("bound");
    }

    [Fact]
    public async Task Declare_is_idempotent()
    {
        var service = "topo" + Guid.NewGuid().ToString("N");
        await using var connection = CreateConnection();
        await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        await Topology.DeclareAsync(channel, service, [TypeName], TestContext.Current.CancellationToken);
        await Topology.DeclareAsync(channel, service, [TypeName], TestContext.Current.CancellationToken);
        channel.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task Retry_queue_dead_letters_back_to_inbox_after_ttl()
    {
        var service = "topo" + Guid.NewGuid().ToString("N");
        await using var connection = CreateConnection();
        await using var channel = await connection.CreateChannelAsync(true, TestContext.Current.CancellationToken);
        await Topology.DeclareAsync(channel, service, [TypeName], TestContext.Current.CancellationToken);
        await channel.BasicPublishAsync("", Topology.RetryQueue(service), "retry"u8.ToArray(), TestContext.Current.CancellationToken);
        BasicGetResult? received = null;
        await Eventually.ShouldAsync(async () => (received = await channel.BasicGetAsync(Topology.InboxQueue(service), true, TestContext.Current.CancellationToken)) is not null, TimeSpan.FromSeconds(7));
        System.Text.Encoding.UTF8.GetString(received!.Body.Span).ShouldBe("retry");
    }

    [Fact]
    public async Task Queues_are_quorum()
    {
        var service = "topo" + Guid.NewGuid().ToString("N");
        await using var connection = CreateConnection();
        await using var setup = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        await Topology.DeclareAsync(setup, service, [TypeName], TestContext.Current.CancellationToken);
        foreach (var queue in Queues(service))
        {
            await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
            var arguments = new Dictionary<string, object?> { ["x-queue-type"] = "classic" };
            if (queue == Topology.RetryQueue(service))
            {
                arguments["x-message-ttl"] = 5000;
                arguments["x-dead-letter-exchange"] = "";
                arguments["x-dead-letter-routing-key"] = Topology.InboxQueue(service);
            }
            var error = await Should.ThrowAsync<OperationInterruptedException>(() => channel.QueueDeclareAsync(queue, true, false, false,
                arguments, cancellationToken: TestContext.Current.CancellationToken));
            error.ShutdownReason!.ReplyCode.ShouldBe((ushort)406);
            error.ShutdownReason.ReplyText.ShouldContain("x-queue-type");
        }
    }

    private RabbitMqConnection CreateConnection() => new(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri });
    private static string[] Queues(string service) => [Topology.InboxQueue(service), Topology.RetryQueue(service), Topology.DeadQueue(service)];
}


