using System.Globalization;
using StackExchange.Redis;

namespace Ledgerline.Reconciliation.Runs;

/// <summary>Step 1 of the run: mutual exclusion per business date. An optimization only; the unique date is the guarantee (ADR 0010).</summary>
public interface IRunLock
{
    /// <summary>
    /// The lock of <paramref name="businessDate"/>, released when the result is disposed, or null when another holder has
    /// it. A lock that cannot be checked at all is not an answer of null: the run proceeds without it.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(DateOnly businessDate, CancellationToken ct);
}

/// <summary>The <c>Reconciliation:Lock</c> section. Defaults per reconciliation.md section 3; tests may shorten them (handoff C3).</summary>
public sealed class LockOptions
{
    public TimeSpan Ttl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often a held lock's TTL is renewed; a third of the TTL unless configured.</summary>
    public TimeSpan RenewInterval { get; init; } = TimeSpan.FromMinutes(10) / 3;

    public static LockOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var ttl = configuration.GetValue<TimeSpan?>("Reconciliation:Lock:Ttl") ?? TimeSpan.FromMinutes(10);
        return new LockOptions { Ttl = ttl, RenewInterval = configuration.GetValue<TimeSpan?>("Reconciliation:Lock:RenewInterval") ?? ttl / 3 };
    }
}

/// <summary>
/// The Redis lock <c>recon:&lt;yyyy-MM-dd&gt;</c>: <c>SET NX PX</c> with a random token, renewed every
/// <see cref="LockOptions.RenewInterval"/> and released only by its holder (compare the token, then <c>PEXPIRE</c> or
/// <c>DEL</c>). Redis unreachable: a warning, and the run goes on without the lock, as the idempotency store does. A lost
/// renewal is logged; nothing depends on it for correctness.
/// </summary>
public sealed partial class RunLock(IConnectionMultiplexer redis, LockOptions options, ILogger<RunLock> logger) : IRunLock
{
    private const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        return 0
        """;
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    public static string KeyFor(DateOnly businessDate) => "recon:" + businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<IAsyncDisposable?> TryAcquireAsync(DateOnly businessDate, CancellationToken ct)
    {
        var key = KeyFor(businessDate);
        var token = Guid.NewGuid().ToString("N");
        try
        {
            if (!await redis.GetDatabase().StringSetAsync(key, token, options.Ttl, When.NotExists)) return null;
        }
        catch (Exception error) when (error is RedisException or TimeoutException)
        {
            LockUnavailable(logger, key, error);
            return NotHeld.Instance;
        }
        return new Held(this, key, token);
    }

    private async Task RenewAsync(string key, string token, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.RenewInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (await TryRenewAsync(key, token)) continue;
                LockLost(logger, key);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>False only when the lock is no longer ours; a Redis failure is logged and the next tick tries again.</summary>
    private async Task<bool> TryRenewAsync(string key, string token)
    {
        try
        {
            return (long)await redis.GetDatabase().ScriptEvaluateAsync(RenewScript, [key], [token, (long)options.Ttl.TotalMilliseconds]) == 1;
        }
        catch (Exception error) when (error is RedisException or TimeoutException)
        {
            RenewFailed(logger, key, error);
            return true;
        }
    }

    private async Task ReleaseAsync(string key, string token)
    {
        try
        {
            await redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [key], [token]);
        }
        catch (Exception error) when (error is RedisException or TimeoutException)
        {
            // The TTL frees the date anyway.
            ReleaseFailed(logger, key, error);
        }
    }

    private sealed class Held : IAsyncDisposable
    {
        private readonly RunLock owner;
        private readonly string key;
        private readonly string token;
        private readonly CancellationTokenSource stop = new();
        private readonly Task renewal;

        public Held(RunLock owner, string key, string token)
        {
            this.owner = owner;
            this.key = key;
            this.token = token;
            renewal = Task.Run(() => owner.RenewAsync(key, token, stop.Token));
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await renewal;
            stop.Dispose();
            await owner.ReleaseAsync(key, token);
        }
    }

    /// <summary>The run proceeds without the lock: Redis could not be asked.</summary>
    private sealed class NotHeld : IAsyncDisposable
    {
        public static readonly NotHeld Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [LoggerMessage(1, LogLevel.Warning, "Redis is unavailable; the run of {Key} proceeds without the lock (the unique business date still guards it)")]
    private static partial void LockUnavailable(ILogger logger, string key, Exception error);
    [LoggerMessage(2, LogLevel.Warning, "The lock {Key} was lost before the run ended; the unique business date still guards it")]
    private static partial void LockLost(ILogger logger, string key);
    [LoggerMessage(3, LogLevel.Warning, "Renewing the lock {Key} failed; the next renewal tries again")]
    private static partial void RenewFailed(ILogger logger, string key, Exception error);
    [LoggerMessage(4, LogLevel.Warning, "Releasing the lock {Key} failed; it expires with its TTL")]
    private static partial void ReleaseFailed(ILogger logger, string key, Exception error);
}
