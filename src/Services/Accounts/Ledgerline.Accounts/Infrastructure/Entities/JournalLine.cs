namespace Ledgerline.Accounts.Infrastructure.Entities;

public sealed class JournalLine
{
    public Guid JournalEntryId { get; set; }
    public int LineNo { get; set; }
    public Guid AccountId { get; set; }
    public string Direction { get; set; } = "";
    public long AmountMinor { get; set; }
    public long BalanceAfterMinor { get; set; }
    public long EventGlobalSeq { get; set; }
}
