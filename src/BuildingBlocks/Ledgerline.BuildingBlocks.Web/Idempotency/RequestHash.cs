using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ledgerline.BuildingBlocks.Web.Idempotency;

public static class RequestHash
{
    public static string Compute(string method, string path, ReadOnlySpan<byte> body)
    {
        using var canonical = new MemoryStream();
        if (!body.IsEmpty)
        {
            using var document = JsonDocument.Parse(body.ToArray());
            using var writer = new Utf8JsonWriter(canonical);
            WriteCanonical(writer, document.RootElement);
            writer.Flush();
        }
        var bytes = Encoding.UTF8.GetBytes(method + path).Concat(canonical.ToArray()).ToArray();
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
