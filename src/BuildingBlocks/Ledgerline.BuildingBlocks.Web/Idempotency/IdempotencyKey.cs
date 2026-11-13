using Microsoft.AspNetCore.Http;

namespace Ledgerline.BuildingBlocks.Web.Idempotency;

public static class IdempotencyKey
{
    public static bool TryRead(HttpContext context, out string key)
    {
        ArgumentNullException.ThrowIfNull(context);
        var values = context.Request.Headers["Idempotency-Key"];
        key = values.ToString();
        return values.Count == 1 && key.Length is >= 1 and <= 64
            && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    }
}
