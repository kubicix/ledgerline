using System.Diagnostics;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Health;
using Ledgerline.BuildingBlocks.Web.Idempotency;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Ledgerline.BuildingBlocks.Web;

public static class WebDefaults
{
    public static WebApplicationBuilder AddLedgerlineWebDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var status = context.ProblemDetails.Status ?? 500;
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
            if (context.ProblemDetails.Type?.StartsWith("urn:ledgerline:problem:", StringComparison.Ordinal) != true)
                context.ProblemDetails.Type = "urn:ledgerline:problem:" + ProblemCodes.ForStatus(status);
            context.ProblemDetails.Detail ??= context.ProblemDetails.Title;
        });
        builder.Services.AddExceptionHandler(options => options.StatusCodeSelector = error =>
            error is BadHttpRequestException requestError ? requestError.StatusCode : StatusCodes.Status500InternalServerError);
        builder.AddLedgerlineJwtAuthentication();
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Customer, policy => policy.RequireRole("customer"))
            .AddPolicy(Policies.Merchant, policy => policy.RequireRole("merchant"))
            .AddPolicy(Policies.Admin, policy => policy.RequireRole("admin"));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379");
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });
        builder.Services.AddScoped<IIdempotencyStore>(provider => new RedisIdempotencyStore(
            provider.GetRequiredService<IConnectionMultiplexer>(), serviceName,
            provider.GetRequiredService<ILogger<RedisIdempotencyStore>>()));
        builder.Services.AddHealthChecks();
        // Kubernetes budget: preStop sleep 5 s + this timeout 25 s = terminationGracePeriodSeconds 30 s.
        // The consumer drain (up to 20 s) runs inside this timeout.
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(25));
        return builder;
    }

    public static WebApplication UseLedgerlineWebDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method)) context.Request.EnableBuffering();
            await next(context);
        });
        app.UseStatusCodePages(context => Results.Problem(statusCode: context.HttpContext.Response.StatusCode).ExecuteAsync(context.HttpContext));
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapLedgerlineHealthEndpoints();
        return app;
    }
}
