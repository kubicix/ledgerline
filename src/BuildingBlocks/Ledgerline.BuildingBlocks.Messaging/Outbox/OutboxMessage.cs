using System.Text.Json;

namespace Ledgerline.BuildingBlocks.Messaging.Outbox;

public sealed class OutboxMessage
{
    public long Sequence { get; set; }
    public Guid MessageId { get; set; }
    public string Type { get; set; } = "";
    public JsonElement Payload { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public string? TraceParent { get; set; }
    public string Transport { get; set; } = "rabbitmq";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}
