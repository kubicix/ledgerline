using StackExchange.Redis;

namespace Ledgerline.Risk.Features.Evaluate;

public sealed class RedisVelocityCounter(IConnectionMultiplexer connection) : IVelocityCounter
{
    public bool IsConnected => connection.IsConnected;
    private const string Script = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1] - ARGV[3] * 1000)
        redis.call('ZADD', KEYS[1], 'NX', ARGV[1], ARGV[2])
        redis.call('EXPIRE', KEYS[1], ARGV[3])
        return redis.call('ZCARD', KEYS[1])
        """;

    public async Task<int> CountAsync(Guid customerId, Guid paymentId, DateTimeOffset at, int windowSeconds, CancellationToken ct) =>
        (int)await connection.GetDatabase().ScriptEvaluateAsync(Script, [$"risk:velocity:{customerId}"],
            [at.ToUnixTimeMilliseconds(), paymentId.ToString(), windowSeconds]).WaitAsync(ct);
}
