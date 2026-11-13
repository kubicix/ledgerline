namespace Ledgerline.Accounts.Infrastructure.Entities;

public sealed class JournalEntry
{
    public Guid Id { get; set; }
    public string EntryType { get; set; } = "";
    public Guid OperationId { get; set; }
    public Guid PaymentId { get; set; }
    public string Currency { get; set; } = "";
    public DateTimeOffset PostedAt { get; set; }
}
