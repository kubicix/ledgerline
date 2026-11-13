using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.BuildingBlocks.Messaging.Outbox;

public sealed class EfOutbox<TDbContext>(TDbContext db, MessageTypeRegistry registry, IClock clock) : IOutbox
    where TDbContext : DbContext
{
    public void Enqueue<T>(T message, Guid correlationId, Guid? causationId = null)
    {
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            MessageId = Ids.New(),
            Type = registry.NameOf<T>(),
            Payload = EnvelopeSerializer.ToPayload(message),
            CorrelationId = correlationId,
            CausationId = causationId,
            TraceParent = Activity.Current?.Id,
            OccurredAt = clock.UtcNow,
            NextAttemptAt = clock.UtcNow
        });
    }
}
