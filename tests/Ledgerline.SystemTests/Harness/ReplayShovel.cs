using System.Collections.Concurrent;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Testing;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Ledgerline.SystemTests.Harness;

/// <summary>One message the shovel saw and re-published twice: once with <see cref="OriginalId"/>, once with <see cref="NewId"/>.</summary>
public sealed record ReplayedMessage(string Exchange, string Type, Guid OriginalId, Guid NewId);

/// <summary>
/// Test-only duplicate generator for docs/testing.md F3. Binds an exclusive, server-named queue with <c>#</c> to the
/// three Ledgerline exchanges and re-publishes every message it receives to the same exchange and routing key twice:
/// first unchanged (same messageId), then with a new messageId in both the envelope (which the consumers' inbox
/// deduplicates on) and the AMQP <c>message-id</c> property. Both copies carry <see cref="ReplayHeader"/>, so the shovel
/// skips its own copies when they come back through its queue; messages a service emits because of a copy are
/// ordinary messages and are replayed too. The original is acknowledged only after both copies were confirmed.
/// Connects to the broker port that Compose publishes on localhost (amqp://localhost:5672).
/// </summary>
public sealed class ReplayShovel : IAsyncDisposable
{
    public const string ReplayHeader = "x-ledgerline-replay";
    public static readonly IReadOnlyList<string> Exchanges = ["ledgerline.accounts", "ledgerline.payments", "ledgerline.risk"];
    private readonly IConnection connection;
    private readonly IChannel consuming;
    private readonly IChannel publishing;
    private readonly string queue;
    private readonly ConcurrentQueue<ReplayedMessage> replayed = new();
    private string? consumerTag;
    private string? failure;
    private int seen;
    private int inProgress;
    private volatile bool stopping;

    private ReplayShovel(IConnection connection, IChannel consuming, IChannel publishing, string queue)
    {
        this.connection = connection;
        this.consuming = consuming;
        this.publishing = publishing;
        this.queue = queue;
    }

    /// <summary>RABBITMQ_URL, or the Compose default user on localhost:5672 (password RABBITMQ_PASSWORD or the local default).</summary>
    public static Uri DefaultUri => new(Environment.GetEnvironmentVariable("RABBITMQ_URL") ??
        $"amqp://ledgerline:{Uri.EscapeDataString(Environment.GetEnvironmentVariable("RABBITMQ_PASSWORD") ?? "ledgerline_dev")}@localhost:5672/");

    /// <summary>Every message received and re-published twice, in the order the shovel finished them.</summary>
    public IReadOnlyList<ReplayedMessage> Replayed => replayed.ToArray();

    /// <summary>Messages received from the exchanges that were not the shovel's own copies.</summary>
    public int Seen => Volatile.Read(ref seen);

    /// <summary>The first error: a copy that could not be built or confirmed, or the consuming channel closing while running.</summary>
    public string? Failure => Volatile.Read(ref failure);

    public static async Task<ReplayShovel> StartAsync(CancellationToken ct = default)
    {
        // No automatic recovery: the queue is exclusive, so a dropped connection loses messages and must fail the test.
        var connection = await new ConnectionFactory { Uri = DefaultUri, AutomaticRecoveryEnabled = false, ClientProvidedName = "ledgerline-systest-replay-shovel" }
            .CreateConnectionAsync(ct);
        try
        {
            var consuming = await connection.CreateChannelAsync(cancellationToken: ct);
            var publishing = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), ct);
            var declared = await consuming.QueueDeclareAsync("", durable: false, exclusive: true, autoDelete: true, cancellationToken: ct);
            foreach (var exchange in Exchanges)
            {
                await consuming.ExchangeDeclarePassiveAsync(exchange, ct);
                await consuming.QueueBindAsync(declared.QueueName, exchange, "#", cancellationToken: ct);
            }
            await consuming.BasicQosAsync(0, 16, false, ct);
            var shovel = new ReplayShovel(connection, consuming, publishing, declared.QueueName);
            consuming.ChannelShutdownAsync += (_, args) =>
            {
                if (!shovel.stopping) shovel.Fail($"The shovel's consuming channel closed: {args.ReplyCode} {args.ReplyText}");
                return Task.CompletedTask;
            };
            var consumer = new AsyncEventingBasicConsumer(consuming);
            consumer.ReceivedAsync += (_, delivery) => shovel.ReplayAsync(delivery);
            shovel.consumerTag = await consuming.BasicConsumeAsync(declared.QueueName, false, consumer, ct);
            return shovel;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Ready messages in a queue (passive declare; unacknowledged deliveries are not counted).</summary>
    public async Task<uint> QueueDepthAsync(string name, CancellationToken ct = default)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        return (await channel.QueueDeclarePassiveAsync(name, ct)).MessageCount;
    }

    /// <summary>
    /// Waits until every copy was consumed. Polls, in this order: (1) for each message type, the consumers that committed
    /// any original of that type (inbox rows across the service databases) have committed every original and every
    /// new-id copy of that type; (2) no outbox row is waiting to be published; (3) the shovel's own queue is empty, no
    /// delivery is being replayed, and nothing was replayed since step 1 began. A message emitted by a copy is in the
    /// outbox when its copy's inbox row commits, so it has reached the shovel by step 3, and its own copies are checked
    /// by the next poll. Same-id copies add no inbox row; each one is published before its new-id twin on the same
    /// channel, so a consumer receives it first. Returns the number of new-id copies that at least one consumer committed.
    /// </summary>
    public async Task<int> WaitUntilReplaysProcessedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var observed = "nothing observed";
        var committed = 0;
        int? quietAt = null;
        try
        {
            await Eventually.ShouldAsync(async () =>
            {
                var snapshot = Replayed;
                var rows = await SystemQueries.InboxRowsAsync(snapshot.SelectMany(x => new[] { x.OriginalId, x.NewId }), ct);
                var consumers = rows.ToLookup(x => x.MessageId, x => x.Consumer);
                var expected = snapshot.GroupBy(x => x.Type)
                    .ToDictionary(x => x.Key, x => x.SelectMany(m => consumers[m.OriginalId]).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
                var missing = snapshot.Where(x => expected[x.Type].Any(c => !consumers[x.OriginalId].Contains(c) || !consumers[x.NewId].Contains(c))).ToArray();
                var pending = await SystemQueries.PendingOutboxMessagesAsync(ct);
                var depth = await QueueDepthAsync(queue, ct);
                var busy = Volatile.Read(ref inProgress);
                observed = $"{missing.Length} of {snapshot.Count} replayed messages not yet consumed everywhere " +
                    $"(for example {string.Join("; ", missing.Take(3).Select(x => $"{x.Type} {x.OriginalId}/{x.NewId}"))}), " +
                    $"{pending} outbox rows pending, shovel queue {depth}, replaying {busy}, failure {Failure ?? "none"}";
                var quiet = missing.Length == 0 && pending == 0 && depth == 0 && busy == 0 && replayed.Count == snapshot.Count;
                // A delivery the broker already handed to the client is neither in the queue depth nor in progress for a
                // moment, so quiet must hold on two consecutive polls with nothing replayed in between.
                var settled = quiet && quietAt == snapshot.Count;
                quietAt = quiet ? snapshot.Count : null;
                committed = snapshot.Count(x => consumers[x.NewId].Any());
                return settled;
            }, timeout);
        }
        catch (ShouldAssertException error)
        {
            throw new ShouldAssertException($"The replayed messages were not all consumed: {observed}. {error.Message}", error);
        }
        return committed;
    }

    public async ValueTask DisposeAsync()
    {
        stopping = true;
        try
        {
            if (consuming.IsOpen && consumerTag is not null) await consuming.BasicCancelAsync(consumerTag);
            await Eventually.ShouldAsync(() => Task.FromResult(Volatile.Read(ref inProgress) == 0), TimeSpan.FromSeconds(10));
        }
        finally
        {
            await publishing.DisposeAsync();
            await consuming.DisposeAsync();
            await connection.DisposeAsync(); // Closing the connection deletes the exclusive queue.
        }
    }

    private async Task ReplayAsync(BasicDeliverEventArgs delivery)
    {
        if (delivery.BasicProperties.Headers?.ContainsKey(ReplayHeader) == true)
        {
            await consuming.BasicAckAsync(delivery.DeliveryTag, false);
            return;
        }
        Interlocked.Increment(ref inProgress);
        Interlocked.Increment(ref seen);
        try
        {
            var body = delivery.Body.ToArray();
            var envelope = EnvelopeSerializer.Deserialize(body);
            var newId = Guid.NewGuid();
            await PublishAsync(delivery, envelope.MessageId, "same-id", body);
            await PublishAsync(delivery, newId, "new-id", EnvelopeSerializer.Serialize(envelope with { MessageId = newId }));
            replayed.Enqueue(new ReplayedMessage(delivery.Exchange, envelope.Type, envelope.MessageId, newId));
        }
        catch (Exception error)
        {
            Fail($"Replaying a message from {delivery.Exchange} ({delivery.RoutingKey}) failed: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            // Acknowledged also after a failure: Failure fails the test, and a requeue would only repeat the same error.
            if (consuming.IsOpen) await consuming.BasicAckAsync(delivery.DeliveryTag, false);
            Interlocked.Decrement(ref inProgress);
        }
    }

    private async Task PublishAsync(BasicDeliverEventArgs delivery, Guid messageId, string kind, byte[] body)
    {
        var properties = new BasicProperties(delivery.BasicProperties)
        {
            MessageId = messageId.ToString(),
            Headers = delivery.BasicProperties.Headers is { } headers ? new Dictionary<string, object?>(headers) : new Dictionary<string, object?>()
        };
        properties.Headers[ReplayHeader] = kind;
        // Publisher confirmation tracking is on, so this returns once the broker confirmed the copy.
        await publishing.BasicPublishAsync(delivery.Exchange, delivery.RoutingKey, false, properties, body);
    }

    private void Fail(string message) => Interlocked.CompareExchange(ref failure, message, null);
}
