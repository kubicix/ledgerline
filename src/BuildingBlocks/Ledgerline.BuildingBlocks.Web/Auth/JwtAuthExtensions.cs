using System.Security.Claims;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Ledgerline.BuildingBlocks.Web.Auth;

public static class JwtAuthExtensions
{
    public static WebApplicationBuilder AddLedgerlineJwtAuthentication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        GuardTestKey(builder.Configuration, builder.Environment);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => Configure(options, builder.Configuration, builder.Environment));
        return builder;
    }

    private static void Configure(JwtBearerOptions options, ConfigurationManager configuration, IHostEnvironment environment)
    {
        GuardTestKey(configuration, environment);
        var mode = configuration["Auth:Mode"] ?? "Oidc";
        var issuer = configuration["Auth:Authority"] ?? throw new InvalidOperationException("Auth:Authority is required.");
        var audience = configuration["Auth:Audience"] ?? throw new InvalidOperationException("Auth:Audience is required.");
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
            ValidAudience = audience, NameClaimType = "sub", RoleClaimType = "roles", ClockSkew = TimeSpan.Zero
        };
        if (mode == "TestKey")
        {
            options.TokenValidationParameters.ValidIssuer = issuer;
            options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(
                configuration["Auth:TestSigningKey"] ?? throw new InvalidOperationException("Auth:TestSigningKey is required.")));
        }
        else if (mode == "Oidc")
        {
            options.Authority = issuer;
            options.RequireHttpsMetadata = configuration.GetValue("Auth:RequireHttpsMetadata", true);
        }
        else throw new InvalidOperationException("Auth:Mode must be Oidc or TestKey.");
        options.Events = CreateEvents();
    }

    private static JwtBearerEvents CreateEvents() => new()
    {
        OnTokenValidated = context =>
        {
            if (!Guid.TryParse(context.Principal?.FindFirstValue("sub"), out _)) context.Fail("A UUID subject is required.");
            return Task.CompletedTask;
        },
        OnChallenge = context =>
        {
            context.HandleResponse();
            return ProblemResults.For(401, ProblemCodes.Unauthenticated, "Authentication required").ExecuteAsync(context.HttpContext);
        },
        OnForbidden = context => ProblemResults.For(403, ProblemCodes.Forbidden, "Access forbidden").ExecuteAsync(context.HttpContext)
    };

    private static void GuardTestKey(ConfigurationManager configuration, IHostEnvironment environment)
    {
        if (configuration["Auth:Mode"] == "TestKey" && !environment.IsEnvironment("Testing"))
            throw new InvalidOperationException("TestKey authentication is allowed only in Testing.");
    }
}
