namespace Ledgerline.Gateway;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers["X-Correlation-Id"].ToString();
        var id = Guid.TryParse(incoming, out _) ? incoming : Guid.CreateVersion7().ToString();
        context.Request.Headers["X-Correlation-Id"] = id;
        context.Response.OnStarting(() => { context.Response.Headers["X-Correlation-Id"] = id; return Task.CompletedTask; });
        return next(context);
    }
}
