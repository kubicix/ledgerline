using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledgerline.BuildingBlocks.Messaging.RabbitMq;

/// <summary>Readiness check for the broker connection, registered by AddLedgerlineMessaging and AddLedgerlineInbox.</summary>
public sealed class RabbitMqHealthCheck(RabbitMqConnection broker) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try { return await broker.IsReadyAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Broker unavailable."); }
        catch (Exception error) { return HealthCheckResult.Unhealthy("Broker unavailable.", error); }
    }
}
