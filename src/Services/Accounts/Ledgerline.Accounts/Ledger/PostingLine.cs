namespace Ledgerline.Accounts.Ledger;

public sealed record PostingLine(Guid AccountId, Direction Direction, long AmountMinor);
