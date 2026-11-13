using System.Diagnostics.Metrics;
using StackExchange.Redis;

namespace Ledgerline.Risk.Features.Evaluate;

public sealed partial class FallbackVelocityCounter(RedisVelocityCounter redis, DatabaseVelocityCounter database,
    ILogger<FallbackVelocityCounter> logger) : IVelocityCounter
{
    private static readonly Meter Meter = new("Ledgerline.Risk");
    private static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("ledgerline.risk.redis_fallback");

    public async Task<int> CountAsync(Guid customerId, Guid paymentId, DateTimeOffset at, int windowSeconds, CancellationToken ct)
    {
        if (redis.IsConnected)
        {
            try { return await redis.CountAsync(customerId, paymentId, at, windowSeconds, ct); }
            catch (RedisException error) { RedisFailed(logger, error); }
        }
        else RedisFailed(logger, null);
        Fallbacks.Add(1);
        return await database.CountAsync(customerId, paymentId, at, windowSeconds, ct);
    }

    [LoggerMessage(1, LogLevel.Warning, "Redis velocity unavailable; counting recorded decisions in the database")]
    private static partial void RedisFailed(ILogger logger, Exception? error);
}
