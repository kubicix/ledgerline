using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Ledgerline.Reporting.Infrastructure;

/// <summary>
/// Readiness of the read model's database only. Kafka is not checked: with Kafka down Reporting still serves
/// (stale) reads (reporting.md section 7), so a broker outage must not take it out of rotation.
/// </summary>
public sealed class ReadinessCheck(MongoCollections collections) : IHealthCheck
{
    private static readonly BsonDocument Ping = new("ping", 1);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await collections.Database.RunCommandAsync<BsonDocument>(Ping, cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception error) { return HealthCheckResult.Unhealthy("Database unavailable.", error); }
    }
}
