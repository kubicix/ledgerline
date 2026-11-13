namespace Ledgerline.Accounts.Ledger;

public sealed record AccountOpened(Guid? OwnerId, string Type, string? SystemCode, string Currency, string? ContactEmail);
public sealed record LedgerEntryPosted(Guid JournalEntryId, EntryType EntryType, Guid PaymentId,
    Direction Direction, long AmountMinor, string Currency, long BalanceAfterMinor);
public sealed record AccountStatusChanged(string Status);
public sealed record LedgerEventMetadata(Guid CorrelationId, Guid? CausationId, Guid OperationId);
