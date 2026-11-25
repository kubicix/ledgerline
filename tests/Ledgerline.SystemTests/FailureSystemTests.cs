using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Ledgerline.SystemTests.Harness;
using Ledgerline.Testing;

namespace Ledgerline.SystemTests;

/// <summary>
/// docs/testing.md section 3, F1 and F2 system rows: a real broker outage and real SIGKILLs of the payments and
/// accounts containers while the workload is in flight. Each test ends with <see cref="SystemInvariants.AssertAsync"/>.
/// Risk's seeded velocity limit (10 per customer per 60 s) is raised once for the collection through
/// <see cref="ComposeStack.EnsureVelocityRaisedAsync"/>, so the workload reaches the bank and the ledger instead of
/// ending Rejected; the stack restores it when the collection ends.
/// </summary>
[Collection(ComposeTests.Name)]
public sealed class FailureSystemTests(ComposeStack stack) : IAsyncDisposable
{
    private const long Amount = 100;
    private const long Funding = 20_000;
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan KillPointTimeout = TimeSpan.FromSeconds(60);

    [Fact(Skip = ComposeStack.SkipReason, SkipUnless = nameof(ComposeStack.Enabled), SkipType = typeof(ComposeStack))]
    public async Task Broker_outage_loses_no_money()
    {
        var ct = TestContext.Current.CancellationToken;
        await stack.UpAsync(ct);
        await stack.EnsureVelocityRaisedAsync(ct);
        using var client = LedgerlineClient.CreateDefault();
        var (alice, bob) = await CustomerWalletsAsync(client, ct);
        var run = await SystemRun.StartAsync(client, [alice, bob], ct);
        var since = await SystemQueries.DatabaseNowAsync(ct);
        await FundAsync(client, run, [alice, bob], ct);

        PaymentDto[] accepted;
        await stack.StopAsync("rabbitmq", ct);
        try
        {
            // 50 top-ups and 50 transfers while the broker is down; every POST must be accepted before it returns.
            var batches = await Task.WhenAll(
                Workloads.TopUpsAsync(client, run, alice, 25, Amount, ct),
                Workloads.TopUpsAsync(client, run, bob, 25, Amount, ct),
                Workloads.TransfersAsync(client, run, alice, bob, 25, Amount, ct),
                Workloads.TransfersAsync(client, run, bob, alice, 25, Amount, ct));
            accepted = batches.SelectMany(x => x).ToArray();
            var states = await SystemQueries.PaymentStatesAsync(accepted.Select(x => x.Id), ct);
            Log($"Accepted during the outage: {accepted.Length}; states with the broker down: {Describe(states)}");
            accepted.Length.ShouldBe(100);
            accepted.ShouldAllBe(x => x.Status == "Processing");
            states.ShouldAllBe(x => x.Status == "Processing" && x.SagaStep == "RiskCheck",
                "Nothing can move past the risk step while the broker is down.");
        }
        finally
        {
            await stack.StartAsync("rabbitmq", ct);
        }

        await SystemInvariants.AssertAsync(client, run, SettleTimeout, ct);
        await AssertEveryRunPaymentCompletedAsync(run, since, ct);
        var deltas = await run.WalletDeltasAsync(client, ct);
        // 25 transfers of the same amount each way: each wallet gains its funding plus its own 25 top-ups.
        deltas[alice.AccountId].ShouldBe(Funding + 25 * Amount);
        deltas[bob.AccountId].ShouldBe(Funding + 25 * Amount);
    }

    [Fact(Skip = ComposeStack.SkipReason, SkipUnless = nameof(ComposeStack.Enabled), SkipType = typeof(ComposeStack))]
    public async Task Killing_payments_mid_saga_completes_after_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        await stack.UpAsync(ct);
        await stack.EnsureVelocityRaisedAsync(ct);
        using var client = LedgerlineClient.CreateDefault();
        var bank = new BankSimulatorClient(stack);
        var (alice, bob) = await CustomerWalletsAsync(client, ct);
        await WithCleanupAsync(async () =>
        {
            var run = await SystemRun.StartAsync(client, [alice, bob], ct);
            var since = await SystemQueries.DatabaseNowAsync(ct);
            // Widens the window in which payments is inside a bank call. A small count keeps the serial payments consumer
            // well inside the 15 s bank step timeout, so no payment exhausts its attempts because of the test itself.
            await bank.SetFaultAsync("Capture", "Delay", count: 5, delayMs: 2000, ct);

            var accepted = (await Task.WhenAll(
                Workloads.TopUpsAsync(client, run, alice, 50, Amount, ct),
                Workloads.TopUpsAsync(client, run, bob, 50, Amount, ct))).SelectMany(x => x).ToArray();
            var ids = accepted.Select(x => x.Id).ToArray();
            ids.Length.ShouldBe(100);

            // Kill point: all 100 top-ups are still mid-saga and at least one is in BankCapture (capture pending or at
            // the bank, where the delay fault holds it). A top-up that is already final can never be un-finalized, so
            // the probe fails at once instead of waiting for the timeout.
            await WaitForKillPointAsync(async () =>
            {
                var states = await SystemQueries.PaymentStatesAsync(ids, ct);
                states.ShouldAllBe(x => x.Status == "Processing", $"A top-up was final before the kill point: {Describe(states)}");
                return (states.Count == ids.Length && states.Any(x => x.SagaStep == "BankCapture"), Describe(states));
            });
            var sinceKillPoint = Stopwatch.StartNew();
            try
            {
                await stack.KillAsync("payments", ct);
                Log($"Kill returned {sinceKillPoint.ElapsedMilliseconds} ms after the kill point was observed.");
                var atKill = await SystemQueries.PaymentStatesAsync(ids, ct);
                await bank.ClearFaultAsync(ct);
                Log($"Top-ups at the kill: {Describe(atKill)}");
                atKill.Count.ShouldBe(ids.Length);
                atKill.ShouldAllBe(x => x.Status == "Processing", "All 100 top-ups must be in flight when payments is killed.");
                atKill.Count(x => x.SagaStep is "BankAuthorize" or "BankCapture" or "LedgerCredit")
                    .ShouldBeGreaterThanOrEqualTo(1, "The kill must hit top-ups past the risk step.");
            }
            finally
            {
                await stack.StartAsync("payments", ct);
            }

            await SystemInvariants.AssertAsync(client, run, SettleTimeout, ct);
            await AssertEveryRunPaymentCompletedAsync(run, since, ct);

            // Bank captures equal ledger top-up entries plus bank refunds; no wallet credited twice.
            var until = await SystemQueries.DatabaseNowAsync(ct);
            var references = ids.Select(x => x.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var settlement = (await bank.GetSettlementsAsync(since, until, ct)).Where(x => references.Contains(x.Reference)).ToArray();
            var captures = settlement.Where(x => x.RecordType == "CAPTURE").ToArray();
            var refunds = settlement.Where(x => x.RecordType == "REFUND").ToArray();
            var ledger = await SystemQueries.JournalEntriesAsync("TopUp", ids, ct);
            Log($"Settlement: {captures.Length} captures, {refunds.Length} refunds; ledger: {ledger.Values.Sum()} TopUp entries for {ids.Length} top-ups.");
            captures.GroupBy(x => x.Reference, StringComparer.OrdinalIgnoreCase).ShouldAllBe(x => x.Count() == 1, "One bank capture per payment.");
            refunds.GroupBy(x => x.Reference, StringComparer.OrdinalIgnoreCase).ShouldAllBe(x => x.Count() == 1, "At most one bank refund per payment.");
            ledger.Values.ShouldAllBe(x => x == 1, "No wallet is credited twice for one top-up.");
            (await SystemQueries.PaymentsCreditedTwiceAsync(ct)).ShouldBeEmpty("No top-up payment has two TopUp journal entries.");
            var captured = captures.Select(x => Guid.Parse(x.Reference)).ToHashSet();
            var credited = ledger.Keys.ToHashSet();
            var refunded = refunds.Select(x => Guid.Parse(x.Reference)).ToHashSet();
            credited.Overlaps(refunded).ShouldBeFalse("A top-up is either credited or refunded at the bank, never both.");
            captured.SetEquals(credited.Union(refunded)).ShouldBeTrue("Bank captures must equal ledger top-up entries plus bank refunds.");
            var amounts = accepted.ToDictionary(x => x.Id, x => x.AmountMinor);
            captures.ShouldAllBe(x => x.AmountMinor == amounts[Guid.Parse(x.Reference)] && x.Currency == Workloads.Currency);
            captures.Length.ShouldBe(ids.Length, "Every top-up completed, so every top-up was captured once.");

            var deltas = await run.WalletDeltasAsync(client, ct);
            deltas[alice.AccountId].ShouldBe(50 * Amount);
            deltas[bob.AccountId].ShouldBe(50 * Amount);
        }, () => bank.ClearFaultAsync(CancellationToken.None));
    }

    [Fact(Skip = ComposeStack.SkipReason, SkipUnless = nameof(ComposeStack.Enabled), SkipType = typeof(ComposeStack))]
    public async Task Killing_accounts_mid_posting_completes_after_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        await stack.UpAsync(ct);
        await stack.EnsureVelocityRaisedAsync(ct);
        using var client = LedgerlineClient.CreateDefault();
        var (alice, bob) = await CustomerWalletsAsync(client, ct);
        var run = await SystemRun.StartAsync(client, [alice, bob], ct);
        var since = await SystemQueries.DatabaseNowAsync(ct);
        await FundAsync(client, run, [alice, bob], ct);

        // 200 transfers, 100 each way; not awaited, so the kill can land while they are still being posted.
        var submitting = Task.WhenAll(
            Workloads.TransfersAsync(client, run, alice, bob, 100, Amount, ct),
            Workloads.TransfersAsync(client, run, bob, alice, 100, Amount, ct));
        Guid[] ids;
        try
        {
            await WaitForKillPointAsync(async () =>
            {
                var transfers = run.Operations.Where(x => x.Kind == "Transfer").Select(x => x.Id).ToArray();
                var posted = (await SystemQueries.JournalEntriesAsync("Transfer", transfers, ct)).Count;
                return (posted >= 20, $"{posted} of {transfers.Length} accepted transfers posted");
            });
            await stack.KillAsync("accounts", ct);
            ids = (await submitting).SelectMany(x => x).Select(x => x.Id).ToArray();
            var postedAtKill = (await SystemQueries.JournalEntriesAsync("Transfer", ids, ct)).Count;
            Log($"Transfers posted at the kill: {postedAtKill} of {ids.Length}; payment states: {Describe(await SystemQueries.PaymentStatesAsync(ids, ct))}");
            ids.Length.ShouldBe(200);
            postedAtKill.ShouldBeInRange(20, 180, "The kill must hit accounts with postings done and postings still to do.");
        }
        catch
        {
            // A failure before the submissions were awaited must not leave them unobserved or lose their own error.
            await LogFailureAsync(submitting, "Submitting the transfers");
            throw;
        }
        finally
        {
            await stack.StartAsync("accounts", ct);
        }

        await SystemInvariants.AssertAsync(client, run, SettleTimeout, ct);
        await AssertEveryRunPaymentCompletedAsync(run, since, ct);
        var entries = await SystemQueries.JournalEntriesAsync("Transfer", ids, ct);
        entries.Count.ShouldBe(ids.Length, "Every completed transfer has a journal entry.");
        entries.Values.ShouldAllBe(x => x == 1, "No transfer is posted twice.");
        var deltas = await run.WalletDeltasAsync(client, ct);
        // 100 transfers of the same amount each way: each wallet ends with its funding.
        deltas[alice.AccountId].ShouldBe(Funding);
        deltas[bob.AccountId].ShouldBe(Funding);
    }

    public async ValueTask DisposeAsync() => await stack.CollectLogsIfFailedAsync();

    private static async Task<(WalletRef Alice, WalletRef Bob)> CustomerWalletsAsync(LedgerlineClient client, CancellationToken ct)
    {
        var wallets = await Workloads.EnsureSmokeWalletsAsync(client, ct);
        return (wallets.Alice, wallets.Bob);
    }

    private static async Task FundAsync(LedgerlineClient client, SystemRun run, IEnumerable<WalletRef> wallets, CancellationToken ct)
    {
        foreach (var wallet in wallets)
        {
            var topUp = await Workloads.TopUpAsync(client, run, wallet, Funding, ct);
            await client.WaitForPaymentAsync(wallet.User, topUp.Id, s => s == "Completed", TimeSpan.FromSeconds(60), ct);
        }
    }

    /// <summary>
    /// Every payment the run created is Completed (none Rejected, Failed or in ManualReview, so the invariants are not
    /// met vacuously), and no payment created with a systest key since the run started is missing from the run.
    /// </summary>
    private static async Task AssertEveryRunPaymentCompletedAsync(SystemRun run, DateTimeOffset since, CancellationToken ct)
    {
        var ids = run.Operations.Select(x => x.Id).ToArray();
        var states = await SystemQueries.PaymentStatesAsync(ids, ct);
        Log($"Final states: {Describe(states)}");
        states.Count.ShouldBe(ids.Length);
        states.ShouldAllBe(x => x.Status == "Completed");
        (await SystemQueries.UnrecordedPaymentsAsync(ids, since, ct)).ShouldBeEmpty("A payment was created that the run did not record.");
    }

    private static async Task WaitForKillPointAsync(Func<Task<(bool Reached, string Observed)>> probe)
    {
        var observed = "nothing observed";
        try
        {
            await Eventually.ShouldAsync(async () =>
            {
                (var reached, observed) = await probe();
                return reached;
            }, KillPointTimeout);
        }
        catch (ShouldAssertException error)
        {
            throw new ShouldAssertException($"The kill point was not reached; last observed: {observed}. {error.Message}", error);
        }
        Log($"Kill point reached: {observed}");
    }

    /// <summary>
    /// Runs the test body, then every cleanup in order. A cleanup failure never hides the body's failure: it is written
    /// to the test output instead. When the body passed, the first cleanup failure fails the test.
    /// </summary>
    private static async Task WithCleanupAsync(Func<Task> body, params Func<Task>[] cleanups)
    {
        ExceptionDispatchInfo? failure = null;
        try { await body(); }
        catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        foreach (var cleanup in cleanups)
        {
            try { await cleanup(); }
            catch (Exception error)
            {
                if (failure is null) failure = ExceptionDispatchInfo.Capture(error);
                else Log($"Cleanup failed after an earlier failure: {error.Message}");
            }
        }
        failure?.Throw();
    }

    /// <summary>Awaits a task on a failure path and writes its own failure to the test output instead of throwing it.</summary>
    private static async Task LogFailureAsync(Task task, string what)
    {
        try { await task; }
        catch (Exception error) { Log($"{what} also failed: {error.Message}"); }
    }

    private static string Describe(IEnumerable<PaymentState> states) => string.Join(", ",
        states.GroupBy(x => $"{x.Status}/{x.SagaStep}").OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Count()}"));

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);
}
