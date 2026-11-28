using Ledgerline.BuildingBlocks.Messaging.RabbitMq;

namespace Ledgerline.BuildingBlocks.Messaging;

/// <summary>
/// Decides which transports an enqueued message goes to. <see cref="IOutbox"/> writes one outbox row per returned
/// record, all in the transaction of the enqueue.
/// </summary>
public interface IOutboxRouting
{
    /// <param name="messageType">The declared type of the enqueued message.</param>
    /// <param name="message">The enqueued message.</param>
    /// <param name="messageId">The <c>message_id</c> of the RabbitMQ row; other rows derive their own IDs from it or are new.</param>
    IReadOnlyList<OutboxRecord> Route(Type messageType, object message, Guid messageId);
}

/// <summary>A further transport for one message type, registered by a transport package.</summary>
public interface IOutboxRoute
{
    Type MessageType { get; }

    /// <summary>The rows to add for one message on this route; each record must carry its own message ID.</summary>
    IEnumerable<OutboxRecord> Records(object message, Guid sourceMessageId);
}

/// <summary>One outbox row to write: its transport, its <c>message_id</c>, and the message with the type that names it.</summary>
public sealed record OutboxRecord(string Transport, Guid MessageId, Type MessageType, object Message);

/// <summary>
/// Every message goes to RabbitMQ; a message type with registered <see cref="IOutboxRoute"/>s also goes to their
/// transports. With no routes registered this is exactly the single RabbitMQ row of Phase 1.
/// </summary>
public sealed class OutboxRouting : IOutboxRouting
{
    public static OutboxRouting RabbitMqOnly { get; } = new([]);

    private readonly Dictionary<Type, IOutboxRoute[]> routes;

    public OutboxRouting(IEnumerable<IOutboxRoute> routes) =>
        this.routes = routes.GroupBy(x => x.MessageType).ToDictionary(x => x.Key, x => x.ToArray());

    public IReadOnlyList<OutboxRecord> Route(Type messageType, object message, Guid messageId)
    {
        var rabbitMq = new OutboxRecord(RabbitMqOutboxPublisher.TransportName, messageId, messageType, message);
        if (!routes.TryGetValue(messageType, out var extra)) return [rabbitMq];
        return [rabbitMq, .. extra.SelectMany(route => route.Records(message, messageId))];
    }
}
