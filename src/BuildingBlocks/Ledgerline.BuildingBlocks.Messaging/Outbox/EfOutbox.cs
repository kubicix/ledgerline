using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.BuildingBlocks.Messaging.Outbox;

/// <summary>
/// Adds the outbox rows of one message to the scoped DbContext: one per record the routing returns, so a message
/// routed to RabbitMQ and Kafka becomes two rows in the same transaction. Without a routing, RabbitMQ only.
/// </summary>
public sealed class EfOutbox<TDbContext>(TDbContext db, MessageTypeRegistry registry, IClock clock,
    IOutboxRouting? routing = null) : IOutbox where TDbContext : DbContext
{
    private readonly IOutboxRouting routing = routing ?? OutboxRouting.RabbitMqOnly;

    public void Enqueue<T>(T message, Guid correlationId, Guid? causationId = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (var record in routing.Route(typeof(T), message, Ids.New()))
        {
            db.Set<OutboxMessage>().Add(new OutboxMessage
            {
                MessageId = record.MessageId,
                Type = registry.NameOf(record.MessageType),
                Payload = EnvelopeSerializer.ToPayload(record.Message, record.MessageType),
                CorrelationId = correlationId,
                CausationId = causationId,
                TraceParent = Activity.Current?.Id,
                Transport = record.Transport,
                OccurredAt = clock.UtcNow,
                NextAttemptAt = clock.UtcNow
            });
        }
    }
}
