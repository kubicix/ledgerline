using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Ledgerline.Gateway;

public sealed class GatewayReadiness(IOptionsMonitor<JwtBearerOptions> options) : IHealthCheck
{
    private volatile bool fetched;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (fetched) return HealthCheckResult.Healthy();
        var jwt = options.Get(JwtBearerDefaults.AuthenticationScheme);
        if (jwt.TokenValidationParameters.IssuerSigningKey is not null) return HealthCheckResult.Healthy();
        try
        {
            var configuration = await jwt.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
            fetched = configuration.SigningKeys.Count > 0;
            return fetched ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Signing keys unavailable.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Signing keys unavailable.");
        }
    }
}
