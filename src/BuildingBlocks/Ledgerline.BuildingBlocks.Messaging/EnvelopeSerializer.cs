using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ledgerline.BuildingBlocks.Messaging;

public static class EnvelopeSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static byte[] Serialize(MessageEnvelope envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    public static MessageEnvelope Deserialize(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<MessageEnvelope>(bytes, Options) ?? throw new JsonException("Envelope cannot be null.");
    public static JsonElement ToPayload<T>(T message) => JsonSerializer.SerializeToElement(message, Options);
    public static JsonElement ToPayload(object message, Type type) => JsonSerializer.SerializeToElement(message, type, Options);
    public static object FromPayload(JsonElement payload, Type type) =>
        payload.Deserialize(type, Options) ?? throw new JsonException("Payload cannot be null.");
}
