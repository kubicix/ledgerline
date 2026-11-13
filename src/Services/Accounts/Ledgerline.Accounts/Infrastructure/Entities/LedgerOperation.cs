namespace Ledgerline.Accounts.Infrastructure.Entities;

public sealed class LedgerOperation
{
    public Guid OperationId { get; set; }
    public string Kind { get; set; } = "";
    public Guid PaymentId { get; set; }
    public string Outcome { get; set; } = "";
    public string? ReasonCode { get; set; }
    public Guid? JournalEntryId { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}
