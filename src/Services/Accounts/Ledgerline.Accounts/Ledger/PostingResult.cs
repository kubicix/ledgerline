namespace Ledgerline.Accounts.Ledger;

public abstract record PostingResult
{
    public sealed record Posted(Guid JournalEntryId, DateTimeOffset PostedAt) : PostingResult;
    public sealed record Rejected(string ReasonCode) : PostingResult;
}
