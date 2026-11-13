using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Testing;
using RabbitMQ.Client;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

[Collection("Broker lifecycle")]
public sealed class PublisherTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Publish_sets_amqp_properties_from_envelope()
    {
        await using var connection = CreateConnection();
        await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        await channel.ExchangeDeclareAsync("ledgerline.publisher", ExchangeType.Topic, durable: true, cancellationToken: TestContext.Current.CancellationToken);
        var queue = await channel.QueueDeclareAsync(cancellationToken: TestContext.Current.CancellationToken);
        await channel.QueueBindAsync(queue.QueueName, "ledgerline.publisher", "#", cancellationToken: TestContext.Current.CancellationToken);
        var message = CreateMessage();
        await new RabbitMqOutboxPublisher(connection).PublishAsync(message, TestContext.Current.CancellationToken);
        var received = await channel.BasicGetAsync(queue.QueueName, true, TestContext.Current.CancellationToken);
        received.ShouldNotBeNull();
        received.BasicProperties.MessageId.ShouldBe(message.MessageId.ToString());
        received.BasicProperties.Type.ShouldBe(message.Type);
        received.BasicProperties.CorrelationId.ShouldBe(message.CorrelationId.ToString());
        received.BasicProperties.ContentType.ShouldBe("application/json");
        received.BasicProperties.Persistent.ShouldBeTrue();
        System.Text.Encoding.UTF8.GetString((byte[])received.BasicProperties.Headers!["traceparent"]!).ShouldBe(message.TraceParent);
        received.BasicProperties.Headers["x-ledgerline-attempt"].ShouldBe(1);
        var envelope = EnvelopeSerializer.Deserialize(received.Body.Span);
        envelope.MessageId.ShouldBe(message.MessageId);
        envelope.CausationId.ShouldBe(message.CausationId);
        envelope.Payload.GetProperty("name").GetString().ShouldBe("published");
    }

    [Fact]
    public async Task Publish_to_exchange_without_binding_throws_PublishFailedException()
    {
        await using var connection = CreateConnection();
        await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        var message = CreateMessage();
        message.Type = "unbound" + Guid.NewGuid().ToString("N") + ".event.test.v1";
        await channel.ExchangeDeclareAsync(MessageTypeRegistry.ExchangeFor(message.Type), ExchangeType.Topic, durable: true, cancellationToken: TestContext.Current.CancellationToken);
        await Should.ThrowAsync<PublishFailedException>(() => new RabbitMqOutboxPublisher(connection).PublishAsync(message, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Publish_when_broker_is_down_throws_PublishFailedException()
    {
        await using var connection = CreateConnection();
        await using var channel = await connection.CreateChannelAsync(false, TestContext.Current.CancellationToken);
        await infrastructure.StopRabbitMqAsync();
        try
        {
            await Should.ThrowAsync<PublishFailedException>(() => new RabbitMqOutboxPublisher(connection).PublishAsync(CreateMessage(), TestContext.Current.CancellationToken));
        }
        finally
        {
            await infrastructure.StartRabbitMqAsync();
        }
    }

    [Fact]
    public async Task Probe_can_publish_and_wait_for_typed_envelopes()
    {
        await using var probe = await BrokerProbe.CreateAsync(infrastructure.RabbitMqUri, "publisher.event.#");
        var correlation = Guid.NewGuid();
        var id = Ids.New();
        await probe.PublishAsync(new ProbeMessage("probe"), correlation, id);
        (await probe.WaitForAsync<ProbeMessage>(p => p.Name == "probe", TimeSpan.FromSeconds(5))).Name.ShouldBe("probe");
        probe.Messages.Single().MessageId.ShouldBe(id);
        probe.Messages.Single().CorrelationId.ShouldBe(correlation);
    }

    private RabbitMqConnection CreateConnection() => new(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri });
    private static OutboxMessage CreateMessage() => new()
    {
        MessageId = Ids.New(), Type = "publisher.event.test.v1", CorrelationId = Ids.New(), CausationId = Ids.New(),
        OccurredAt = DateTimeOffset.UtcNow, Payload = EnvelopeSerializer.ToPayload(new { Name = "published" }),
        TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
    };
    [MessageType("publisher.event.probe.v1")]
    public sealed record ProbeMessage(string Name);
}
