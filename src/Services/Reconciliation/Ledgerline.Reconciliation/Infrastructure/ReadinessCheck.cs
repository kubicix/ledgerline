using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledgerline.Reconciliation.Infrastructure;

/// <summary>Readiness of the service's own database. Kafka is not a readiness dependency (handoff C4).</summary>
public sealed class ReadinessCheck(ReconciliationDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try { return await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database unavailable."); }
        catch (Exception error) { return HealthCheckResult.Unhealthy("Database unavailable.", error); }
    }
}
