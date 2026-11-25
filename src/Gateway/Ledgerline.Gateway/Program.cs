using Ledgerline.BuildingBlocks.Web;
using Ledgerline.BuildingBlocks.Web.Telemetry;
using Ledgerline.Gateway;
using Yarp.ReverseProxy.Forwarder;
using Microsoft.AspNetCore.Http.Timeouts;

var builder = WebApplication.CreateBuilder(args);
builder.AddLedgerlineTelemetry("gateway");
builder.AddLedgerlineWebDefaults("gateway");
builder.AddGatewayRateLimiting();
builder.Services.AddRequestTimeouts();
builder.Services.AddHealthChecks().AddCheck<GatewayReadiness>("jwks", timeout: TimeSpan.FromSeconds(5));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5100").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
var app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRouting();
app.UseCors();
app.UseLedgerlineWebDefaults();
app.UseRateLimiter();
app.UseRequestTimeouts();
app.Use(RequestBodyLimit.EnforceAsync);
app.MapReverseProxy(proxy =>
{
    proxy.Use(async (context, next) =>
    {
        await next(context);
        if (context.Response.HasStarted || context.GetForwarderErrorFeature() is not { } error) return;
        var timedOut = error.Error == ForwarderError.RequestTimedOut ||
            context.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken.IsCancellationRequested == true;
        var status = timedOut ? 504 : 503;
        context.Response.Clear();
        await Results.Problem(statusCode: status, title: status == 504 ? "Upstream timed out" : "Upstream unavailable").ExecuteAsync(context);
    });
    proxy.UseSessionAffinity();
    proxy.UseLoadBalancing();
    proxy.UsePassiveHealthChecks();
}).RequireRateLimiting("api");
app.Run();

namespace Ledgerline.Gateway
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
