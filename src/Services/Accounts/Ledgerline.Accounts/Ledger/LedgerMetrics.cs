using System.Diagnostics.Metrics;
using Ledgerline.Accounts.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Accounts.Ledger;

public sealed class LedgerMetricsOptions
{
    /// <summary>How often the cached trial balance behind the imbalance gauge is refreshed.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Ledger instruments of the ADR 0009 table, on the meter <c>Ledgerline</c> from the host's <see cref="IMeterFactory"/>.
/// The imbalance gauge reads a cached trial balance (sum of balances per currency, which must be zero), refreshed in
/// the background, so a metrics scrape never queries the database.
/// </summary>
public sealed partial class LedgerMetrics : BackgroundService
{
    private static readonly double[] SecondBuckets = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private readonly IServiceScopeFactory scopes;
    private readonly LedgerMetricsOptions options;
    private readonly ILogger<LedgerMetrics> logger;
    private readonly Histogram<double> postingDuration;
    private volatile IReadOnlyDictionary<string, long> trialBalance = new Dictionary<string, long>();
    private long refreshes;

    public LedgerMetrics(IMeterFactory meterFactory, IServiceScopeFactory scopes, LedgerMetricsOptions options, ILogger<LedgerMetrics> logger)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        this.scopes = scopes;
        this.options = options;
        this.logger = logger;
        var meter = meterFactory.Create("Ledgerline");
        postingDuration = meter.CreateHistogram<double>("ledgerline.ledger.posting.duration", "s",
            "Time to post one ledger operation, including waiting for account row locks.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = SecondBuckets });
        meter.CreateObservableGauge("ledgerline.ledger.imbalance", ObserveImbalance, "{minor_unit}",
            "Sum of account balances per currency in minor units, from the cached trial balance. Must be zero.");
    }

    /// <summary>Completed trial balance refreshes; lets callers wait for a reading taken after a known point in time.</summary>
    public long Refreshes => Interlocked.Read(ref refreshes);

    /// <summary>Outcome is Succeeded or Rejected (as on LedgerOperation), or Failed when the posting threw.</summary>
    internal void RecordPosting(TimeSpan elapsed, string kind, string outcome) => postingDuration.Record(elapsed.TotalSeconds,
        new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("outcome", outcome));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.RefreshInterval);
        try
        {
            do
            {
                try { await RefreshAsync(stoppingToken); }
                // Any failure, including a cancellation not caused by shutdown, is logged; a metrics loop never stops the host.
                catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested) { RefreshFailed(logger, error); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        trialBalance = await db.Accounts.AsNoTracking().GroupBy(x => x.Currency)
            .Select(group => new { Currency = group.Key, Sum = group.Sum(x => x.BalanceMinor) })
            .ToDictionaryAsync(x => x.Currency, x => x.Sum, ct);
        Interlocked.Increment(ref refreshes);
    }

    private IEnumerable<Measurement<long>> ObserveImbalance() =>
        trialBalance.Select(pair => new Measurement<long>(pair.Value, new KeyValuePair<string, object?>("currency", pair.Key)));

    [LoggerMessage(1, LogLevel.Warning, "Trial balance refresh failed; the imbalance gauge keeps its last value.")]
    private static partial void RefreshFailed(ILogger logger, Exception error);
}
