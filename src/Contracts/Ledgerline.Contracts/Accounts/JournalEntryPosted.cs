using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Accounts;

[MessageType("accounts.event.journal-entry-posted.v1")]
public sealed record JournalEntryPosted(Guid JournalEntryId, string EntryType, Guid PaymentId, string Currency,
    DateTimeOffset PostedAt, IReadOnlyList<JournalEntryPostedLine> Lines);
public sealed record JournalEntryPostedLine(Guid AccountId, string Direction, long AmountMinor, long BalanceAfterMinor);
