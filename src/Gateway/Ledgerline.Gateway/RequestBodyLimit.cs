using Microsoft.AspNetCore.Http.Features;

namespace Ledgerline.Gateway;

internal static class RequestBodyLimit
{
    private const int MaximumBytes = 65536;

    internal static async Task EnforceAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = MaximumBytes;
        if (context.Request.ContentLength > MaximumBytes || await UnknownLengthExceedsLimitAsync(context))
        {
            await Results.Problem(statusCode: 413, title: "Request body too large").ExecuteAsync(context);
            return;
        }
        await next(context);
    }

    private static async Task<bool> UnknownLengthExceedsLimitAsync(HttpContext context)
    {
        if (context.Request.ContentLength is not null || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true) return false;
        context.Request.EnableBuffering();
        var buffer = new byte[8192];
        var total = 0;
        try
        {
            while (total <= MaximumBytes)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumBytes + 1 - total)), context.RequestAborted);
                if (read == 0) break;
                total += read;
            }
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return true; }
        finally { context.Request.Body.Position = 0; }
        return total > MaximumBytes;
    }
}
