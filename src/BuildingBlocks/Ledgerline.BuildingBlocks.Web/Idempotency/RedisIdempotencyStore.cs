using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Ledgerline.BuildingBlocks.Web.Idempotency;

public sealed class RedisIdempotencyStore(IConnectionMultiplexer connection, string serviceName,
    ILogger<RedisIdempotencyStore> logger) : IIdempotencyStore
{
    private static readonly Meter Meter = new("Ledgerline.Idempotency");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("idempotency.redis.failures");
    private static readonly Action<ILogger, Exception?> LogFailure = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, "RedisUnavailable"),
        "Redis idempotency unavailable; the database remains authoritative");
    private readonly Dictionary<string, string> leases = [];
    private const string BeginScript = """
        local value = redis.call('GET', KEYS[1])
        if not value then
            redis.call('SET', KEYS[1], 'in-progress:' .. ARGV[1], 'EX', 86400)
            return 'started'
        end
        return value
        """;
    private const string CompleteScript = """
        if redis.call('GET', KEYS[1]) == 'in-progress:' .. ARGV[1] then
            redis.call('SET', KEYS[1], 'done:' .. ARGV[1] .. ':' .. ARGV[2], 'EX', 86400)
        end
        return 0
        """;
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == 'in-progress:' .. ARGV[1] then
            redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    public async Task<IdempotencyBegin> TryBeginAsync(string scope, string key, string hash)
    {
        var redisKey = Key(scope, key);
        try
        {
            var value = (string?)await connection.GetDatabase().ScriptEvaluateAsync(BeginScript, [redisKey], [hash]);
            if (value == "started")
            {
                leases[redisKey] = hash;
                return new IdempotencyBegin.Started();
            }
            if (value == "in-progress:" + hash) return new IdempotencyBegin.InProgress();
            var prefix = "done:" + hash + ":";
            if (value?.StartsWith(prefix, StringComparison.Ordinal) == true &&
                Guid.TryParse(value.AsSpan(prefix.Length), out var id)) return new IdempotencyBegin.Done(id);
            return new IdempotencyBegin.HashMismatch();
        }
        catch (RedisException error)
        {
            RecordFailure(error);
            return new IdempotencyBegin.Unavailable();
        }
    }

    public async Task CompleteAsync(string scope, string key, string hash, Guid resourceId)
    {
        try
        {
            await connection.GetDatabase().ScriptEvaluateAsync(CompleteScript, [Key(scope, key)], [hash, resourceId.ToString()]);
        }
        catch (RedisException error) { RecordFailure(error); }
    }

    public async Task ReleaseAsync(string scope, string key)
    {
        var redisKey = Key(scope, key);
        if (!leases.Remove(redisKey, out var hash)) return;
        try
        {
            await connection.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [redisKey], [hash]);
        }
        catch (RedisException error) { RecordFailure(error); }
    }

    private string Key(string scope, string key) => $"idem:{serviceName}:{scope}:{key}";

    private void RecordFailure(RedisException error)
    {
        Failures.Add(1);
        LogFailure(logger, error);
    }
}
