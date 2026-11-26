using System.Diagnostics;
using Ledgerline.SystemTests.Harness;
using Ledgerline.Testing;

namespace Ledgerline.SystemTests;

/// <summary>
/// docs/testing.md section 3, F3 system row: a test-only shovel (<see cref="ReplayShovel"/>) re-publishes every message
/// seen on the three Ledgerline exchanges during a 60 s mixed workload, once with the same messageId and once with a new
/// one. After every replay was consumed, final balances and payment statuses equal the run's expected values, no
/// payment is posted or captured twice, nothing was dead-lettered, and the trial balance is zero.
/// </summary>
[Collection(ComposeTests.Name)]
public sealed class ReplaySystemTests(ComposeStack stack) : IAsyncDisposable
{
    private const long Funding = 100_000;
    private const int Workers = 4;
    private static readonly TimeSpan WorkloadDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromMinutes(3);
    private static readonly string[] Services = ["accounts", "payments", "risk", "notifications"];

    // Every command type the workload must have sent through the shovel, so a broken binding cannot pass unnoticed.
    private static readonly string[] RequiredTypes =
    [
        "risk.command.evaluate-risk.v1", "risk.event.risk-approved.v1",
        "payments.command.call-bank.v1",
        "accounts.command.credit-top-up.v1", "accounts.command.transfer-funds.v1", "accounts.command.reserve-funds.v1",
        "accounts.command.capture-funds.v1", "accounts.command.release-funds.v1", "accounts.command.refund-funds.v1",
        "accounts.event.ledger-operation-succeeded.v1"
    ];

    [Fact(Skip = ComposeStack.SkipReason, SkipUnless = nameof(ComposeStack.Enabled), SkipType = typeof(ComposeStack))]
    public async Task Replaying_every_message_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await stack.UpAsync(ct);
        await stack.EnsureVelocityRaisedAsync(ct);
        using var client = LedgerlineClient.CreateDefault();
        var wallets = await Workloads.EnsureSmokeWalletsAsync(client, ct);
        var run = await SystemRun.StartAsync(client, wallets.All, ct);
        var since = await SystemQueries.DatabaseNowAsync(ct);

        await using var shovel = await ReplayShovel.StartAsync(ct);
        var deadBefore = await DeadQueueDepthsAsync(shovel, ct);
        var expected = new Expectations();
        foreach (var customer in new[] { wallets.Alice, wallets.Bob })
        {
            var funding = await Workloads.TopUpAsync(client, run, customer, Funding, ct);
            await client.WaitForPaymentAsync(customer.User, funding.Id, s => s == "Completed", StepTimeout, ct);
            expected.Completed(funding.Id, null, customer, Funding);
        }

        // 60 s of mixed traffic. Each worker waits for its scenario to end before it starts the next one, so the
        // workload paces itself and never has more than Workers payments in flight.
        var next = -1;
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(async _ =>
        {
            while (clock.Elapsed < WorkloadDuration)
                await RunScenarioAsync(Interlocked.Increment(ref next), client, run, wallets, expected, ct);
        }));
        Log($"Workload: {next + 1} scenarios in {clock.Elapsed.TotalSeconds:F0} s, {run.Operations.Count} run operations.");

        await SystemInvariants.WaitUntilRunIsFinalAsync(client, run, SettleTimeout, ct);
        var waited = Stopwatch.StartNew();
        var committed = await shovel.WaitUntilReplaysProcessedAsync(SettleTimeout, ct);
        var replayed = shovel.Replayed;
        Log($"All copies consumed after {waited.Elapsed.TotalSeconds:F1} s; consumers committed {committed} new-id copies (an inbox row each).");
        committed.ShouldBeGreaterThan(0, "No new-id copy reached a consumer, so the replay proved nothing.");
        Log($"Replayed {replayed.Count} messages twice each: {string.Join(", ", replayed.GroupBy(x => x.Exchange).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Count()}"))}");
        Log($"By type: {string.Join(", ", replayed.GroupBy(x => x.Type).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Count()}"))}");
        shovel.Failure.ShouldBeNull();
        shovel.Seen.ShouldBe(replayed.Count, "Every message the shovel received was re-published with the same and with a new messageId.");
        replayed.Select(x => x.NewId).Distinct().Count().ShouldBe(replayed.Count);
        replayed.ShouldAllBe(x => x.NewId != x.OriginalId);
        foreach (var exchange in ReplayShovel.Exchanges)
            replayed.Count(x => x.Exchange == exchange).ShouldBeGreaterThan(0, $"Nothing was replayed from {exchange}.");
        foreach (var type in RequiredTypes)
            replayed.Count(x => x.Type == type).ShouldBeGreaterThan(0, $"No {type} was replayed.");

        await SystemInvariants.AssertAsync(client, run, SettleTimeout, ct);

        // Payment and refund statuses, captured and refunded amounts: exactly what the run expects.
        foreach (var operation in run.Operations.Where(x => x.Kind != "Refund"))
        {
            var payment = await client.GetPaymentAsync(KeycloakTokens.Admin, operation.Id, ct);
            payment.Status.ShouldBe(expected.Status[operation.Id], $"{operation.Kind} {operation.Id}");
            if (expected.Merchant.TryGetValue(operation.Id, out var amounts))
            {
                payment.CapturedMinor.ShouldBe(amounts.Captured, $"Captured amount of {operation.Id}");
                payment.RefundedMinor.ShouldBe(amounts.Refunded, $"Refunded amount of {operation.Id}");
            }
        }
        foreach (var refund in run.Operations.Where(x => x.Kind == "Refund"))
            (await client.ListRefundsAsync(KeycloakTokens.Admin, refund.PaymentId!.Value, ct)).Items.Single(x => x.Id == refund.Id).Status
                .ShouldBe(expected.Status[refund.Id], $"Refund {refund.Id}");
        (await SystemQueries.UnrecordedPaymentsAsync(run.Operations.Select(x => x.Id), since, ct)).ShouldBeEmpty("A payment was created that the run did not record.");

        var deltas = await run.WalletDeltasAsync(client, ct);
        foreach (var wallet in run.Wallets)
            deltas[wallet.AccountId].ShouldBe(expected.Delta(wallet.AccountId), $"Balance change of {wallet.User}'s wallet");

        // No journal entry was posted twice for one payment, and none is missing.
        foreach (var (entryType, payments) in expected.JournalEntries)
        {
            var entries = await SystemQueries.JournalEntriesAsync(entryType, payments.Keys, ct);
            foreach (var (payment, count) in payments)
                entries.GetValueOrDefault(payment).ShouldBe(count, $"{entryType} journal entries of payment {payment}");
        }
        (await SystemQueries.PaymentsCreditedTwiceAsync(ct)).ShouldBeEmpty("No top-up payment has two TopUp journal entries.");

        // The bank captured every top-up once, and refunded none.
        var topUps = run.Operations.Where(x => x.Kind == "TopUp").ToDictionary(x => x.Id.ToString(), x => x.AmountMinor, StringComparer.OrdinalIgnoreCase);
        var settlement = (await new BankSimulatorClient(stack).GetSettlementsAsync(since, await SystemQueries.DatabaseNowAsync(ct), ct))
            .Where(x => topUps.ContainsKey(x.Reference)).ToArray();
        settlement.ShouldAllBe(x => x.RecordType == "CAPTURE", "No top-up was refunded at the bank.");
        settlement.GroupBy(x => x.Reference, StringComparer.OrdinalIgnoreCase).ShouldAllBe(x => x.Count() == 1, "One bank capture per top-up.");
        settlement.Length.ShouldBe(topUps.Count, "Every top-up was captured at the bank.");
        settlement.ShouldAllBe(x => x.AmountMinor == topUps[x.Reference] && x.Currency == Workloads.Currency);

        (await DeadQueueDepthsAsync(shovel, ct)).ShouldBe(deadBefore, "A replayed message was dead-lettered.");
    }

    public async ValueTask DisposeAsync() => await stack.CollectLogsIfFailedAsync();

    /// <summary>
    /// One scenario per call, chosen round robin: top-ups, transfers both ways, and merchant payments that end captured in
    /// full and partly refunded, captured in part, voided, or captured automatically. Each ends final; none stays Authorized.
    /// </summary>
    private static async Task RunScenarioAsync(int index, LedgerlineClient client, SystemRun run, SmokeWallets wallets,
        Expectations expected, CancellationToken ct)
    {
        var (alice, bob, shop) = (wallets.Alice, wallets.Bob, wallets.Shop);
        switch (index % 8)
        {
            case 0: await TopUpAsync(alice, 100); break;
            case 1: await TopUpAsync(bob, 100); break;
            case 2: await TransferAsync(alice, bob, 100); break;
            case 3: await TransferAsync(bob, alice, 100); break;
            case 4:
            {
                var payment = await AuthorizedAsync(alice, 300);
                await client.CaptureAsync(shop.User, payment.Id, 300, ct: ct);
                await client.WaitForPaymentAsync(alice.User, payment.Id, s => s == "Captured", StepTimeout, ct);
                var refund = await client.RefundAsync(shop.User, payment.Id, 100, ct: ct);
                run.Record(new RunOperation("Refund", refund.Id, shop.User, 100, Workloads.Currency, payment.Id));
                await WaitForRefundAsync(client, shop.User, payment.Id, refund.Id, ct);
                expected.Captured(payment.Id, alice, shop, 300, 100);
                expected.Completed(refund.Id, shop, alice, 100, "Refund", payment.Id);
                break;
            }
            case 5:
            {
                var payment = await AuthorizedAsync(bob, 300);
                await client.CaptureAsync(shop.User, payment.Id, 200, ct: ct);
                await client.WaitForPaymentAsync(bob.User, payment.Id, s => s == "Captured", StepTimeout, ct);
                expected.Captured(payment.Id, bob, shop, 200, 0);
                break;
            }
            case 6:
            {
                var payment = await AuthorizedAsync(alice, 300);
                await client.PostAsync<PaymentDto>(shop.User, $"/api/payments/{payment.Id}/void", new { }, LedgerlineClient.NewKey("void"), ct);
                await client.WaitForPaymentAsync(alice.User, payment.Id, s => s == "Voided", StepTimeout, ct);
                expected.Voided(payment.Id);
                break;
            }
            default:
            {
                var payment = await MerchantAsync(bob, 200, "Automatic");
                await client.WaitForPaymentAsync(bob.User, payment.Id, s => s == "Captured", StepTimeout, ct);
                expected.Captured(payment.Id, bob, shop, 200, 0);
                break;
            }
        }

        async Task TopUpAsync(WalletRef wallet, long amount)
        {
            var payment = await Workloads.TopUpAsync(client, run, wallet, amount, ct);
            await client.WaitForPaymentAsync(wallet.User, payment.Id, s => s == "Completed", StepTimeout, ct);
            expected.Completed(payment.Id, null, wallet, amount);
        }

        async Task TransferAsync(WalletRef from, WalletRef to, long amount)
        {
            var payment = await Workloads.TransferAsync(client, run, from, to, amount, ct);
            await client.WaitForPaymentAsync(from.User, payment.Id, s => s == "Completed", StepTimeout, ct);
            expected.Completed(payment.Id, from, to, amount, "Transfer");
        }

        async Task<PaymentDto> AuthorizedAsync(WalletRef customer, long amount)
        {
            var payment = await MerchantAsync(customer, amount, "Manual");
            return await client.WaitForPaymentAsync(customer.User, payment.Id, s => s == "Authorized", StepTimeout, ct);
        }

        async Task<PaymentDto> MerchantAsync(WalletRef customer, long amount, string captureMode)
        {
            var payment = await client.CreateMerchantPaymentAsync(customer.User, customer.AccountId, shop.AccountId, amount, Workloads.Currency, captureMode, ct: ct);
            run.Record(new RunOperation("MerchantPayment", payment.Id, customer.User, amount, Workloads.Currency));
            return payment;
        }
    }

    private static async Task WaitForRefundAsync(LedgerlineClient client, string user, Guid paymentId, Guid refundId, CancellationToken ct)
    {
        var status = "unknown";
        await Eventually.ShouldAsync(async () =>
        {
            status = (await client.ListRefundsAsync(user, paymentId, ct)).Items.Single(x => x.Id == refundId).Status;
            if (status == "Failed") throw new ShouldAssertException($"Refund {refundId} failed.");
            return status == "Completed";
        }, StepTimeout, $"Refund {refundId} did not complete.");
    }

    private static async Task<IReadOnlyDictionary<string, uint>> DeadQueueDepthsAsync(ReplayShovel shovel, CancellationToken ct)
    {
        var depths = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        foreach (var service in Services)
            depths[service + ".inbox.dead"] = await shovel.QueueDepthAsync(service + ".inbox.dead", ct);
        return depths;
    }

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    /// <summary>What the run expects, recorded by each scenario once it ended: statuses, amounts, wallet changes, journal entries.</summary>
    private sealed class Expectations
    {
        private readonly Lock gate = new();
        private readonly Dictionary<Guid, long> deltas = [];
        private readonly Dictionary<string, Dictionary<Guid, int>> entries = new(StringComparer.Ordinal);

        public Dictionary<Guid, string> Status { get; } = [];

        public Dictionary<Guid, (long Captured, long Refunded)> Merchant { get; } = [];

        public IReadOnlyDictionary<string, Dictionary<Guid, int>> JournalEntries => entries;

        public long Delta(Guid accountId) => deltas.GetValueOrDefault(accountId);

        /// <summary>A top-up (from is null), a transfer, or a refund (entry type Refund, journal entry on the merchant payment).</summary>
        public void Completed(Guid id, WalletRef? from, WalletRef to, long amount, string entryType = "TopUp", Guid? paymentId = null)
        {
            lock (gate)
            {
                Status[id] = "Completed";
                Move(from, to, amount);
                Entry(entryType, paymentId ?? id);
            }
        }

        /// <summary>Only the captured amount moves; the rest of the hold goes back to the customer. A refund is recorded separately.</summary>
        public void Captured(Guid id, WalletRef customer, WalletRef merchant, long captured, long refunded)
        {
            lock (gate)
            {
                Status[id] = "Captured";
                Merchant[id] = (captured, refunded);
                Move(customer, merchant, captured);
                Entry("Hold", id);
                Entry("Capture", id);
            }
        }

        public void Voided(Guid id)
        {
            lock (gate)
            {
                Status[id] = "Voided";
                Merchant[id] = (0, 0);
                Entry("Hold", id);
                Entry("Release", id);
            }
        }

        private void Move(WalletRef? from, WalletRef to, long amount)
        {
            if (from is not null) deltas[from.AccountId] = deltas.GetValueOrDefault(from.AccountId) - amount;
            deltas[to.AccountId] = deltas.GetValueOrDefault(to.AccountId) + amount;
        }

        private void Entry(string entryType, Guid paymentId)
        {
            if (!entries.TryGetValue(entryType, out var counts)) entries[entryType] = counts = [];
            counts[paymentId] = counts.GetValueOrDefault(paymentId) + 1;
        }
    }
}
