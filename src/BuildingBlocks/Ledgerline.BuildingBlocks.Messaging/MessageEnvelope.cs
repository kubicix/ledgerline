using System.Text.Json;

namespace Ledgerline.BuildingBlocks.Messaging;

public sealed record MessageEnvelope(Guid MessageId, string Type, DateTimeOffset OccurredAt,
    Guid CorrelationId, Guid? CausationId, string? TraceParent, JsonElement Payload);
