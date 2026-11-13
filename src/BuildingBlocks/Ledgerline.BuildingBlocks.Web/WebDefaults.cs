using System.Diagnostics;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Health;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledgerline.BuildingBlocks.Web;

public static class WebDefaults
{
    public static WebApplicationBuilder AddLedgerlineWebDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if ((builder.Configuration["Auth:Mode"] ?? "DevHeaders") != "DevHeaders")
            throw new InvalidOperationException("M1 requires Auth:Mode=DevHeaders.");
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
            throw new InvalidOperationException("Development header authentication is allowed only in Development or Testing.");
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var status = context.ProblemDetails.Status ?? 500;
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
            if (context.ProblemDetails.Type?.StartsWith("urn:ledgerline:problem:", StringComparison.Ordinal) != true)
                context.ProblemDetails.Type = "urn:ledgerline:problem:" + ProblemCodes.ForStatus(status);
            context.ProblemDetails.Detail ??= context.ProblemDetails.Title;
        });
        builder.Services.AddAuthentication(DevHeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DevHeaderAuthenticationHandler>(DevHeaderAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Customer, policy => policy.RequireRole("customer"))
            .AddPolicy(Policies.Merchant, policy => policy.RequireRole("merchant"))
            .AddPolicy(Policies.Admin, policy => policy.RequireRole("admin"));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, CurrentUser>();
        builder.Services.AddHealthChecks();
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
