using System.Collections.Concurrent;

namespace Ledgerline.SystemTests.Harness;

public sealed record WalletRef(string User, Guid AccountId, string Currency);

public sealed record SmokeWallets(WalletRef Alice, WalletRef Bob, WalletRef Shop)
{
    public IReadOnlyList<WalletRef> All => [Alice, Bob, Shop];
}

/// <summary>A payment or refund this run created, with the user who can read it.</summary>
public sealed record RunOperation(string Kind, Guid Id, string User, long AmountMinor, string Currency, Guid? PaymentId = null);

/// <summary>
/// What one test run did: the wallets it uses (balances snapshotted before the workload) and every payment and refund
/// it created. Volumes persist across runs, so invariants about money are computed over this run's deltas.
/// </summary>
public sealed class SystemRun
{
    private readonly ConcurrentQueue<RunOperation> operations = new();
    private readonly Dictionary<Guid, (WalletRef Wallet, long Owned)> before;

    private SystemRun(Dictionary<Guid, (WalletRef, long)> before) => this.before = before;

    public IReadOnlyList<WalletRef> Wallets => before.Values.Select(x => x.Wallet).ToArray();

    public IReadOnlyList<RunOperation> Operations => operations.ToArray();

    /// <summary>
    /// Waits until no payment or refund is in flight anywhere (an earlier test may have left some settling), then
    /// snapshots balance plus held amount of every wallet the run will touch. Call before the workload starts.
    /// </summary>
    public static async Task<SystemRun> StartAsync(LedgerlineClient client, IEnumerable<WalletRef> wallets, CancellationToken ct = default)
    {
        await SystemInvariants.WaitUntilNothingInFlightAsync(SystemInvariants.DefaultTimeout, ct);
        var snapshot = new Dictionary<Guid, (WalletRef, long)>();
        foreach (var wallet in wallets)
            snapshot.Add(wallet.AccountId, (wallet, Owned(await client.GetAccountAsync(wallet.User, wallet.AccountId, ct))));
        return new SystemRun(snapshot);
    }

    public void Record(RunOperation operation) => operations.Enqueue(operation);

    /// <summary>Per wallet: (balance + held) now minus (balance + held) at <see cref="StartAsync"/>.</summary>
    public async Task<IReadOnlyDictionary<Guid, long>> WalletDeltasAsync(LedgerlineClient client, CancellationToken ct = default)
    {
        var deltas = new Dictionary<Guid, long>();
        foreach (var (id, (wallet, owned)) in before)
            deltas[id] = checked(Owned(await client.GetAccountAsync(wallet.User, id, ct)) - owned);
        return deltas;
    }

    private static long Owned(AccountDto account) => checked(account.BalanceMinor + account.HeldMinor);
}

/// <summary>Workloads driven through the gateway. Each call records what it created in the <see cref="SystemRun"/>.</summary>
public static class Workloads
{
    public const string Currency = "TRY";

    public static async Task<SmokeWallets> EnsureSmokeWalletsAsync(LedgerlineClient client, CancellationToken ct = default)
    {
        var alice = await client.EnsureWalletAsync(KeycloakTokens.Alice, "CustomerWallet", Currency, ct);
        var bob = await client.EnsureWalletAsync(KeycloakTokens.Bob, "CustomerWallet", Currency, ct);
        var shop = await client.EnsureWalletAsync(KeycloakTokens.Shop, "MerchantWallet", Currency, ct);
        return new SmokeWallets(new(KeycloakTokens.Alice, alice.Id, Currency), new(KeycloakTokens.Bob, bob.Id, Currency),
            new(KeycloakTokens.Shop, shop.Id, Currency));
    }

    /// <summary>
    /// The scripts/smoke.sh happy path: top-up 10000 to Alice, transfer 2500 to Bob, merchant payment 3000 (manual capture),
    /// capture 2000, refund 500. Expected wallet deltas: Alice +6000, Bob +2500, Shop +1500.
    /// </summary>
    public static async Task SmokeFlowAsync(LedgerlineClient client, SystemRun run, SmokeWallets wallets, CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromSeconds(60);
        var topUp = await TopUpAsync(client, run, wallets.Alice, 10_000, ct);
        await client.WaitForPaymentAsync(wallets.Alice.User, topUp.Id, s => s == "Completed", timeout, ct);
        var transfer = await TransferAsync(client, run, wallets.Alice, wallets.Bob, 2_500, ct);
        await client.WaitForPaymentAsync(wallets.Alice.User, transfer.Id, s => s == "Completed", timeout, ct);
        var merchant = await client.CreateMerchantPaymentAsync(wallets.Alice.User, wallets.Alice.AccountId, wallets.Shop.AccountId, 3_000, Currency, "Manual", ct: ct);
        run.Record(new RunOperation("MerchantPayment", merchant.Id, wallets.Alice.User, 3_000, Currency));
        await client.WaitForPaymentAsync(wallets.Alice.User, merchant.Id, s => s is "Authorized" or "Captured", timeout, ct);
        await client.CaptureAsync(wallets.Shop.User, merchant.Id, 2_000, ct: ct);
        await client.WaitForPaymentAsync(wallets.Alice.User, merchant.Id, s => s == "Captured", timeout, ct);
        var refund = await client.RefundAsync(wallets.Shop.User, merchant.Id, 500, ct: ct);
        run.Record(new RunOperation("Refund", refund.Id, wallets.Shop.User, 500, Currency, merchant.Id));
        await Ledgerline.Testing.Eventually.ShouldAsync(async () =>
            (await client.ListRefundsAsync(wallets.Shop.User, merchant.Id, ct)).Items.Any(r => r.Id == refund.Id && r.Status == "Completed"),
            timeout, $"Refund {refund.Id} did not complete.");
    }

    public static async Task<PaymentDto> TopUpAsync(LedgerlineClient client, SystemRun run, WalletRef wallet, long amountMinor, CancellationToken ct = default)
    {
        var payment = await client.TopUpAsync(wallet.User, wallet.AccountId, amountMinor, wallet.Currency, ct: ct);
        run.Record(new RunOperation("TopUp", payment.Id, wallet.User, amountMinor, wallet.Currency));
        return payment;
    }

    public static async Task<PaymentDto> TransferAsync(LedgerlineClient client, SystemRun run, WalletRef from, WalletRef to, long amountMinor, CancellationToken ct = default)
    {
        var payment = await client.TransferAsync(from.User, from.AccountId, to.AccountId, amountMinor, from.Currency, ct: ct);
        run.Record(new RunOperation("Transfer", payment.Id, from.User, amountMinor, from.Currency));
        return payment;
    }

    /// <summary>
    /// At most this many POSTs of one bulk call are open at a time. Every service shares one Postgres (max_connections 100)
    /// and each open POST holds a pooled payments connection, so an unbounded burst of 100 POSTs exhausts the server's
    /// connection slots (SQLSTATE 53300) and Payments answers 500.
    /// </summary>
    public const int MaxConcurrentRequests = 8;

    /// <summary>Starts <paramref name="count"/> top-ups, <see cref="MaxConcurrentRequests"/> at a time, and returns once all were accepted (not completed).</summary>
    public static Task<PaymentDto[]> TopUpsAsync(LedgerlineClient client, SystemRun run, WalletRef wallet, int count, long amountMinor, CancellationToken ct = default) =>
        BoundedAsync(count, () => TopUpAsync(client, run, wallet, amountMinor, ct), ct);

    /// <summary>Starts <paramref name="count"/> transfers, <see cref="MaxConcurrentRequests"/> at a time, and returns once all were accepted (not completed).</summary>
    public static Task<PaymentDto[]> TransfersAsync(LedgerlineClient client, SystemRun run, WalletRef from, WalletRef to, int count, long amountMinor, CancellationToken ct = default) =>
        BoundedAsync(count, () => TransferAsync(client, run, from, to, amountMinor, ct), ct);

    private static async Task<PaymentDto[]> BoundedAsync(int count, Func<Task<PaymentDto>> start, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(MaxConcurrentRequests);
        return await Task.WhenAll(Enumerable.Range(0, count).Select(async _ =>
        {
            await slots.WaitAsync(ct);
            try { return await start(); }
            finally { slots.Release(); }
        }));
    }
}
