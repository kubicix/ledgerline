using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Ledgerline.Payments.Features.Payments;

internal sealed record ResourceCursor(DateTimeOffset At, Guid Id)
{
    internal string Encode() => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(this));

    internal static bool TryDecode(string? value, out ResourceCursor? cursor)
    {
        cursor = null;
        if (value is null) return true;
        try
        {
            cursor = JsonSerializer.Deserialize<ResourceCursor>(WebEncoders.Base64UrlDecode(value));
            return cursor is not null && cursor.Id != Guid.Empty && cursor.At.Offset == TimeSpan.Zero;
        }
        catch (FormatException) { return false; }
        catch (JsonException) { return false; }
    }
}
