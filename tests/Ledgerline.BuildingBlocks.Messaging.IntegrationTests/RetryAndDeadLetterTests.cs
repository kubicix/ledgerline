using System.Collections.Concurrent;
using System.Text;
using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.BuildingBlocks.Messaging.IntegrationTests.Support;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

public sealed class RetryAndDeadLetterTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Failing_handler_is_retried_through_retry_queue_with_incremented_attempt()
    {
        var cs = await CreateDatabaseAsync();
        var service = "retry" + Ids.New().ToString("N");
        var state = new HandlerState();
        using var host = CreateHost(cs, service, state);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await WaitConsumerAsync(service);
        var envelope = Envelope(2);
        await PublishRawAsync(service, EnvelopeSerializer.Serialize(envelope));
        await Eventually.ShouldAsync(async () => await InboxCountAsync(cs) == 1);
        await host.StopAsync(TestContext.Current.CancellationToken);
        state.Attempts.ShouldBe([1, 2, 3]);
        await using var db = OpenDb(cs);
        (await db.Widgets.SingleAsync(TestContext.Current.CancellationToken)).Id.ShouldBe(envelope.MessageId);
    }

    [Fact]
    public async Task Poison_message_goes_to_dead_queue_after_max_attempts()
    {
        var cs = await CreateDatabaseAsync();
        var service = "retry" + Ids.New().ToString("N");
        var state = new HandlerState();
        using var host = CreateHost(cs, service, state);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await WaitConsumerAsync(service);
        var envelope = Envelope(int.MaxValue);
        await PublishRawAsync(service, EnvelopeSerializer.Serialize(envelope));
        var dead = await ReadDeadAsync(service);
        await host.StopAsync(TestContext.Current.CancellationToken);
        state.Attempts.ShouldBe([1, 2, 3]);
        dead.BasicProperties.Headers!["x-ledgerline-attempt"].ShouldBe(3);
        EnvelopeSerializer.Deserialize(dead.Body.Span).MessageId.ShouldBe(envelope.MessageId);
        (await InboxCountAsync(cs)).ShouldBe(0);
        await AssertQueueEmptyAsync(Topology.InboxQueue(service));
        await AssertQueueEmptyAsync(Topology.DeadQueue(service));
    }

    [Fact]
    public async Task Unknown_message_type_is_dead_lettered_immediately()
    {
        var cs = await CreateDatabaseAsync();
        var service = "retry" + Ids.New().ToString("N");
        var state = new HandlerState();
        using var host = CreateHost(cs, service, state);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await WaitConsumerAsync(service);
        await PublishRawAsync(service, EnvelopeSerializer.Serialize(Envelope(0) with { Type = "retry.command.unknown.v1" }));
        var dead = await ReadDeadAsync(service);
        await host.StopAsync(TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString((byte[])dead.BasicProperties.Headers!["x-ledgerline-dead-reason"]!).ShouldBe("unknown-type");
        state.Attempts.ShouldBeEmpty();
        (await InboxCountAsync(cs)).ShouldBe(0);
    }

    [Fact]
    public async Task Unparseable_body_is_dead_lettered_immediately()
    {
        var cs = await CreateDatabaseAsync();
        var service = "retry" + Ids.New().ToString("N");
        var state = new HandlerState();
        using var host = CreateHost(cs, service, state);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await WaitConsumerAsync(service);
        await PublishRawAsync(service, "not-json"u8.ToArray());
        var dead = await ReadDeadAsync(service);
        await host.StopAsync(TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString((byte[])dead.BasicProperties.Headers!["x-ledgerline-dead-reason"]!).ShouldBe("invalid-envelope");
        Encoding.UTF8.GetString(dead.Body.Span).ShouldBe("not-json");
        state.Attempts.ShouldBeEmpty();
        (await InboxCountAsync(cs)).ShouldBe(0);
    }

    private IHost CreateHost(string cs, string service, HandlerState state)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri }));
        builder.Services.AddSingleton(new ConsumerOptions { Service = service, Prefetch = 1, MaxAttempts = 3, RetryTtlMs = 200 });
        builder.Services.AddSingleton<MessageTypeRegistry>();
        builder.Services.AddSingleton(sp => new HandlerRegistry(sp.GetRequiredService<MessageTypeRegistry>()).Add<RetryMessage, RetryHandler>());
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<ICrashPoint, NoCrashPoint>();
        builder.Services.AddSingleton<IAckStrategy, DefaultAckStrategy>();
        builder.Services.AddSingleton(state);
        builder.Services.AddDbContext<TestDbContext>(options => options.UseNpgsql(cs));
        builder.Services.AddScoped<RetryHandler>();
        builder.Services.AddHostedService<ConsumerHost<TestDbContext>>();
        return builder.Build();
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var cs = await infrastructure.CreateDatabaseAsync("retry");
        await using var db = OpenDb(cs);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return cs;
    }

    private static TestDbContext OpenDb(string cs) => new(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(cs).Options);
    private static MessageEnvelope Envelope(int failures) => new(Ids.New(), "retry.command.fail.v1", DateTimeOffset.UtcNow,
        Ids.New(), null, null, EnvelopeSerializer.ToPayload(new RetryMessage(failures)));

    private async Task PublishRawAsync(string service, byte[] body)
    {
        await using var connection = new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri });
        await using var channel = await connection.CreateChannelAsync(true, TestContext.Current.CancellationToken);
        await channel.BasicPublishAsync("", Topology.InboxQueue(service), true,
            new BasicProperties { Persistent = true, Headers = new Dictionary<string, object?> { ["x-ledgerline-attempt"] = 1 } },
            body, TestContext.Current.CancellationToken);
    }

    private async Task WaitConsumerAsync(string service) => await Eventually.ShouldAsync(async () =>
    {
        await using var connection = new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri });
        await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        try { return (await channel.QueueDeclarePassiveAsync(Topology.InboxQueue(service), TestContext.Current.CancellationToken)).ConsumerCount == 1; }
        catch (OperationInterruptedException) { return false; }
    });

    private async Task<BasicGetResult> ReadDeadAsync(string service)
    {
        await using var connection = new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri });
        await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        BasicGetResult? dead = null;
        await Eventually.ShouldAsync(async () => (dead = await channel.BasicGetAsync(Topology.DeadQueue(service), true, TestContext.Current.CancellationToken)) is not null);
        return dead!;
    }

    private static async Task<int> InboxCountAsync(string cs)
    {
        await using var db = OpenDb(cs);
        return await db.Set<InboxMessage>().CountAsync(TestContext.Current.CancellationToken);
    }

    private async Task AssertQueueEmptyAsync(string queue)
    {
        await using var connection = new RabbitMqConnection(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri });
        await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        (await channel.QueueDeclarePassiveAsync(queue, TestContext.Current.CancellationToken)).MessageCount.ShouldBe(0u);
    }

    public sealed class HandlerState
    {
        public ConcurrentQueue<int> Attempts { get; } = new();
    }

    public sealed class RetryHandler(TestDbContext db, HandlerState state) : IMessageHandler<RetryMessage>
    {
        public Task HandleAsync(RetryMessage message, MessageContext context, CancellationToken ct)
        {
            state.Attempts.Enqueue(context.Attempt);
            if (context.Attempt <= message.Failures) throw new InvalidOperationException("injected failure");
            db.Widgets.Add(new Widget(context.MessageId, "handled"));
            return Task.CompletedTask;
        }
    }

    [MessageType("retry.command.fail.v1")]
    public sealed record RetryMessage(int Failures);
}
