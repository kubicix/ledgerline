using System.Diagnostics;
using System.Globalization;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using PostingLedger = Ledgerline.Accounts.Ledger.Ledger;

namespace Ledgerline.Accounts.IntegrationTests.Benchmarks;

/// <summary>
/// The monolith baseline of docs/when-monolith.md: the same transfer posting the Accounts ledger performs, run as one
/// local transaction per transfer (no broker, no saga, no HTTP). Opt-in with LEDGERLINE_BENCH=1. The numbers go to the
/// test output; <c>dotnet test</c> shows a passing test's output with <c>--output Detailed</c>.
/// </summary>
public sealed class LocalTransferBaseline(InfrastructureFixture infrastructure)
{
    public const string SkipReason = "The local transfer baseline runs only when LEDGERLINE_BENCH=1.";
    private const int Transfers = 10_000;
    private const int Workers = 16;
    // Same shape as transfer-steady.js: 200 customer wallets, transfers between different wallets, small amounts.
    private const int Wallets = 200;
    private const long FundingMinor = 1_000_000;
    private const string Currency = "TRY";

    public static bool Enabled => Environment.GetEnvironmentVariable("LEDGERLINE_BENCH") == "1";

    [Fact(Skip = SkipReason, SkipUnless = nameof(Enabled), SkipType = typeof(LocalTransferBaseline))]
    public async Task Local_transfers_report_p50_p95_and_throughput()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await DatabaseFactory.CreateMigratedAsync<AccountsDbContext>(infrastructure, "bench", c => MigrateCommand.RunAsync(c, ct));
        var options = new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(cs).Options;
        var wallets = new Guid[Wallets];
        await using (var db = new AccountsDbContext(options))
            for (var i = 0; i < Wallets; i++) wallets[i] = await TestAccounts.OpenWalletAsync(db, Ids.New(), Currency);
        // Funding top-ups use the same posting path, so they also warm up EF Core and the connection pool. Not measured.
        await RunAsync(Wallets, i => Entries.TopUp(Ids.New(), Ids.New(), wallets[i], Money.Create(FundingMinor, Currency)), options, ct);

        // Pairs by index: transfer i debits wallet i mod 200 (50 transfers of at most 100 each, far below its funding)
        // and credits the wallet 16 to 183 places further on. The workers run about 16 consecutive indices at a time,
        // and no two of those share a wallet, so the baseline has no row-lock contention by construction (the hot
        // account is transfer-hot-account.js). Every transfer must post; a rejection would inflate the throughput.
        var watch = Stopwatch.StartNew();
        var latencies = await RunAsync(Transfers, i =>
        {
            var from = i % Wallets;
            var to = (from + Workers + i / Wallets % (Wallets - 2 * Workers)) % Wallets;
            return Entries.Transfer(Ids.New(), Ids.New(), wallets[from], wallets[to], Money.Create(1 + i % 100, Currency));
        }, options, ct);
        watch.Stop();

        Array.Sort(latencies);
        var p50 = Percentile(latencies, 0.50);
        var p95 = Percentile(latencies, 0.95);
        var perSecond = Transfers / watch.Elapsed.TotalSeconds;
        Log(string.Create(CultureInfo.InvariantCulture,
            $"LocalTransferBaseline: {Transfers} transfers, {Workers} workers, p50 {p50.TotalMilliseconds:F2} ms, p95 {p95.TotalMilliseconds:F2} ms, {perSecond:F0} transfers/s"));
        await LedgerAssert.InvariantsHoldAsync(cs);
    }

    /// <summary>Posts <paramref name="count"/> requests with <see cref="Workers"/> parallel workers; returns each one's latency.</summary>
    private static async Task<TimeSpan[]> RunAsync(int count, Func<int, PostingRequest> request, DbContextOptions<AccountsDbContext> options, CancellationToken ct)
    {
        var latencies = new TimeSpan[count];
        var next = -1;
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Task.Run(async () =>
        {
            for (var i = Interlocked.Increment(ref next); i < count; i = Interlocked.Increment(ref next))
            {
                var started = Stopwatch.GetTimestamp();
                await using var db = new AccountsDbContext(options);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var result = await new PostingLedger(db, new EventStore(db), new SystemClock()).PostAsync(request(i), ct);
                await transaction.CommitAsync(ct);
                latencies[i] = Stopwatch.GetElapsedTime(started);
                result.ShouldBeOfType<PostingResult.Posted>();
            }
        }, ct)));
        return latencies;
    }

    /// <summary>Nearest-rank percentile of an ascending array.</summary>
    private static TimeSpan Percentile(TimeSpan[] sorted, double fraction) =>
        sorted[Math.Max(0, (int)Math.Ceiling(fraction * sorted.Length) - 1)];

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);
}
