using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledgerline.Notifications.Infrastructure;

/// <summary>Readiness of the service's own database. The broker check comes from messaging (RabbitMqHealthCheck).</summary>
public sealed class ReadinessCheck(NotificationsDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try { return await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database unavailable."); }
        catch (Exception error) { return HealthCheckResult.Unhealthy("Database unavailable.", error); }
    }
}
