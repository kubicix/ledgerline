using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Ledgerline.BuildingBlocks.Web.Health;

public static class HealthEndpoints
{
    public static WebApplication MapLedgerlineHealthEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        // From ApplicationStopping on, readiness answers 503 before any check runs, so the answer never waits on the
        // database or the broker while the host drains.
        var stopping = app.Lifetime.ApplicationStopping;
        var ready = ((IEndpointRouteBuilder)app).CreateApplicationBuilder()
            .Use((context, next) => stopping.IsCancellationRequested ? NotReadyAsync(context) : next(context))
            .UseMiddleware<HealthCheckMiddleware>(Options.Create(new HealthCheckOptions()))
            .Build();
        app.Map("/health/ready", ready).WithDisplayName("Health checks").AllowAnonymous();
        return app;
    }

    private static Task NotReadyAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store, no-cache";
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync("Unhealthy");
    }
}
