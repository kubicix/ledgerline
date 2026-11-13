using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Ledgerline.BuildingBlocks.Web.Problems;

namespace Ledgerline.Gateway;

internal static class RateLimiting
{
    internal static void AddGatewayRateLimiting(this WebApplicationBuilder builder)
    {
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("api", context => RateLimitPartition.GetTokenBucketLimiter(
                context.User.FindFirstValue("sub") ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = builder.Configuration.GetValue("RateLimiting:TokenLimit", 100),
                    TokensPerPeriod = builder.Configuration.GetValue("RateLimiting:TokensPerPeriod", 50),
                    ReplenishmentPeriod = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimiting:PeriodSeconds", 1)),
                    AutoReplenishment = true, QueueLimit = 0
                }));
            options.OnRejected = async (context, _) =>
            {
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Ceiling(retry.TotalSeconds) : 1;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: 429, type: "urn:ledgerline:problem:" + ProblemCodes.RateLimited,
                    title: "Rate limit exceeded").ExecuteAsync(context.HttpContext);
            };
        });
    }
}
