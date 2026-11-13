using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledgerline.Notifications.Infrastructure;

public sealed class ReadinessCheck(NotificationsDbContext db, RabbitMqConnection broker) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken)) return HealthCheckResult.Unhealthy("Database unavailable.");
            return await broker.IsReadyAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Broker unavailable.");
        }
        catch (Exception error) { return HealthCheckResult.Unhealthy("Dependency unavailable.", error); }
    }
}
