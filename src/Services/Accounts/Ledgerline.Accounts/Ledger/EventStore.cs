using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Accounts.Ledger;

public sealed class EventStore(AccountsDbContext db)
{
    public LedgerEvent Append(Guid accountId, int version, LedgerEntryPosted payload, LedgerEventMetadata metadata, DateTimeOffset occurredAt)
    {
        var entry = new LedgerEvent
        {
            StreamId = accountId, Version = version, Type = "ledger-entry-posted.v1", OccurredAt = occurredAt,
            Data = EnvelopeSerializer.ToPayload(payload), Metadata = EnvelopeSerializer.ToPayload(metadata)
        };
        db.Events.Add(entry);
        return entry;
    }
}
