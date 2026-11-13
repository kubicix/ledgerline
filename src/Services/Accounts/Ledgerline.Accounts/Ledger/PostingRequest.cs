namespace Ledgerline.Accounts.Ledger;

public sealed record PostingRequest(Guid OperationId, Guid PaymentId, EntryType EntryType,
    string Currency, IReadOnlyList<PostingLine> Lines, IReadOnlySet<Guid> SkipStatusCheckFor);
