using System.Text;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Ledgerline.BuildingBlocks.Web.Idempotency;

public sealed class IdempotencyEndpointFilter : IEndpointFilter
{
    public const string KeyItem = "Ledgerline.IdempotencyKey";
    public const string HashItem = "Ledgerline.RequestHash";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        if (!IdempotencyKey.TryRead(http, out var key)) return ProblemResults.IdempotencyKeyMissing();
        http.Request.Body.Position = 0;
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(http.RequestAborted);
        http.Request.Body.Position = 0;
        http.Items[KeyItem] = key;
        http.Items[HashItem] = RequestHash.Compute(http.Request.Method, http.Request.Path.Value!, Encoding.UTF8.GetBytes(body));
        return await next(context);
    }
}

public static class IdempotencyEndpointExtensions
{
    public static RouteHandlerBuilder RequireIdempotencyKey(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<IdempotencyEndpointFilter>();
}
