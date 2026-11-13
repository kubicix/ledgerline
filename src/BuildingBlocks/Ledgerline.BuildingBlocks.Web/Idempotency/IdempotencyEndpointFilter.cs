using System.Text;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Ledgerline.BuildingBlocks.Web.Idempotency;

public sealed class IdempotencyEndpointFilter(IIdempotencyStore store, ICurrentUser user) : IEndpointFilter
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
        var hash = RequestHash.Compute(http.Request.Method, http.Request.Path.Value!, Encoding.UTF8.GetBytes(body));
        http.Items[HashItem] = hash;
        var scope = user.Id.ToString();
        var begin = await store.TryBeginAsync(scope, key!, hash);
        if (begin is IdempotencyBegin.HashMismatch) return ProblemResults.IdempotencyKeyReused();
        if (begin is IdempotencyBegin.InProgress)
        {
            http.Response.Headers.RetryAfter = "1";
            return ProblemResults.For(409, ProblemCodes.RequestInProgress, "Request in progress");
        }
        if (begin is not IdempotencyBegin.Started) return await next(context);
        return await ExecuteStartedAsync(context, next, scope, key!, hash);
    }

    private async ValueTask<object?> ExecuteStartedAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next, string scope, string key, string hash)
    {
        try
        {
            var result = await next(context);
            if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 } &&
                result is IValueHttpResult { Value: IIdempotentResource resource })
                await store.CompleteAsync(scope, key, hash, resource.Id);
            return result;
        }
        finally { await store.ReleaseAsync(scope, key); }
    }
}

public static class IdempotencyEndpointExtensions
{
    public static RouteHandlerBuilder RequireIdempotencyKey(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<IdempotencyEndpointFilter>();
}
