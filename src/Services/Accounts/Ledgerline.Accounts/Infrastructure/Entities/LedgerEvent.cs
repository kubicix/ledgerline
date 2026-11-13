using System.Text.Json;

namespace Ledgerline.Accounts.Infrastructure.Entities;

public sealed class LedgerEvent
{
    public long GlobalSeq { get; set; }
    public Guid StreamId { get; set; }
    public int Version { get; set; }
    public string Type { get; set; } = "";
    public JsonElement Data { get; set; }
    public JsonElement Metadata { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
