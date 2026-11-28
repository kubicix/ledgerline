using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.SystemTests.Harness;
using Ledgerline.Testing;

namespace Ledgerline.SystemTests;

/// <summary>
/// Phase 2 (M5) on its own stack shape (<see cref="StackShape.Phase2"/>: compose.yaml plus compose.phase2.yaml, profiles
/// app and phase2). Accounts and Payments also publish to Kafka; Reporting projects the stream into MongoDB;
/// Reconciliation stages it and matches it with the bank's settlement file. Workloads go through the gateway.
/// </summary>
[Collection(Phase2ComposeTests.Name)]
public sealed class Phase2SystemTests(Phase2ComposeStack fixture) : IAsyncDisposable
{
    private const string OperationsEmail = "operations@ledgerline.test"; // Notifications__OperationsEmail in compose.phase2.yaml
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CatchUpTimeout = TimeSpan.FromMinutes(2);
    // The run waits for its Kafka inputs up to Reconciliation:Run:InputWaitTimeout (5 min by default), then matches.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(7);
    private readonly ComposeStack stack = fixture.Stack;

    [Fact(Skip = ComposeStack.SkipReason, SkipUnless = nameof(ComposeStack.Enabled), SkipType = typeof(ComposeStack))]
    public async Task Read_model_catches_up_and_matches_ledger_after_workload()
    {
        var ct = TestContext.Current.CancellationToken;
        await stack.UpAsync(ct);
        await stack.EnsureVelocityRaisedAsync(ct);
        using var client = LedgerlineClient.CreateDefault();
        var wallets = await Workloads.EnsureSmokeWalletsAsync(client, ct);
        WalletRef[] touched = [wallets.Alice, wallets.Bob];
        var run = await SystemRun.StartAsync(client, touched, ct);

        var topUps = (await Workloads.TopUpsAsync(client, run, wallets.Alice, 3, 5_000, ct))
            .Concat(await Workloads.TopUpsAsync(client, run, wallets.Bob, 2, 4_000, ct)).ToArray();
        foreach (var topUp in topUps)
            await client.WaitForPaymentAsync(KeycloakTokens.Admin, topUp.Id, s => s == "Completed", StepTimeout, ct);
        var transfers = (await Workloads.TransfersAsync(client, run, wallets.Alice, wallets.Bob, 3, 1_000, ct))
            .Concat(await Workloads.TransfersAsync(client, run, wallets.Bob, wallets.Alice, 2, 700, ct)).ToArray();
        foreach (var transfer in transfers)
            await client.WaitForPaymentAsync(KeycloakTokens.Admin, transfer.Id, s => s == "Completed", StepTimeout, ct);
        await SystemInvariants.AssertAsync(client, run, ct: ct);

        // The newest statement line of each wallet carries the balance Accounts reports, and it belongs to this run, so an
        // earlier line that happened to carry the same balance cannot pass for a caught-up read model.
        var ours = run.Operations.Select(x => x.Id).ToHashSet();
        foreach (var wallet in touched)
        {
            var account = await client.GetAccountAsync(wallet.User, wallet.AccountId, ct);
            StatementLineDto? latest = null;
            await Eventually.ShouldAsync(async () =>
            {
                var page = await client.FindAsync<PageDto<StatementLineDto>>(wallet.User, $"/api/reporting/accounts/{wallet.AccountId}/statement?limit=1", ct);
                latest = page is { Items.Count: > 0 } ? page.Items[0] : null;
                return latest is not null && latest.BalanceAfterMinor == account.BalanceMinor && ours.Contains(latest.PaymentId);
            }, CatchUpTimeout,
                $"The statement of {wallet.User}'s wallet did not reach the ledger balance {account.BalanceMinor} (latest line: {latest}).");
        }
        await LedgerAssert.InvariantsHoldAsync(SystemInvariants.AccountsDatabase);
    }

    /// <remarks>
    /// Reconciles the current UTC day, which is not over yet: the run does not wait for records past midnight but for
    /// every partition to have nothing left to read (the quiet-partition rule of its Kafka wait). A workload that crosses
    /// midnight UTC splits across two business dates and fails this test; rerun it after midnight. A Completed run with
    /// complete inputs is final for its date, so on kept volumes this test passes once per UTC day and project.
    /// </remarks>
    [Fact(Skip = ComposeStack.SkipReason, SkipUnless = nameof(ComposeStack.Enabled), SkipType = typeof(ComposeStack))]
    public async Task Reconciliation_of_today_is_clean_after_workload_and_flags_a_tampered_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await stack.UpAsync(ct);
        await stack.EnsureVelocityRaisedAsync(ct);
        using var client = LedgerlineClient.CreateDefault();
        var bank = new BankSimulatorClient(stack);
        var today = DateOnly.FromDateTime((await SystemQueries.DatabaseNowAsync(ct)).UtcDateTime);
        var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        (await client.FindAsync<RunDto>(KeycloakTokens.Admin, $"/api/reconciliation/runs/{date}", ct)).ShouldBeNull(
            $"Project {stack.Shape.ProjectName} already has a reconciliation run for {date} on its kept volumes, and a completed day " +
            "cannot be run again. Use a fresh project (LEDGERLINE_SYSTEM_TESTS_PHASE2_PROJECT) or run after midnight UTC.");
        var wallets = await Workloads.EnsureSmokeWalletsAsync(client, ct);
        var run = await SystemRun.StartAsync(client, [wallets.Alice], ct);

        var topUps = await Workloads.TopUpsAsync(client, run, wallets.Alice, 3, 2_500, ct);
        foreach (var topUp in topUps)
            await client.WaitForPaymentAsync(KeycloakTokens.Admin, topUp.Id, s => s == "Completed", StepTimeout, ct);
        await SystemInvariants.AssertAsync(client, run, ct: ct);

        var tampered = topUps[0].Id;
        var capture = (await bank.GetSettlementAsync(today, ct))
            .Single(x => x.RecordType == "CAPTURE" && Guid.TryParse(x.Reference, out var id) && id == tampered);
        await bank.DropCaptureFromSettlementAsync(today, capture.BankId, ct);
        (await bank.GetSettlementAsync(today, ct)).ShouldNotContain(x => x.BankId == capture.BankId);
        // Every event of the workload is in Kafka before the run starts. A quiet partition is otherwise indistinguishable
        // from one whose last record is still in an outbox, and the run would match without it.
        await Eventually.ShouldAsync(async () => await SystemQueries.PendingOutboxMessagesAsync(ct) == 0, StepTimeout,
            "The outboxes of Accounts, Payments and Risk did not drain.");

        await client.PostAsync<JsonElement>(KeycloakTokens.Admin, "/api/reconciliation/runs", new { businessDate = date }, LedgerlineClient.NewKey("recon"), ct);
        RunDto? result = null;
        await Eventually.ShouldAsync(async () =>
        {
            result = await client.FindAsync<RunDto>(KeycloakTokens.Admin, $"/api/reconciliation/runs/{date}", ct);
            if (result?.Status == "Failed") throw new ShouldAssertException($"The reconciliation run of {date} failed: {result.Error}");
            return result?.Status == "Completed";
        }, RunTimeout, $"The reconciliation run of {date} did not complete (last seen: {result}).");

        result!.InputsIncomplete.ShouldBeFalse();
        var items = await client.GetAsync<IReadOnlyList<ItemDto>>(KeycloakTokens.Admin, $"/api/reconciliation/runs/{date}/items", ct);
        var item = items.ShouldHaveSingleItem();
        item.ShouldBe(new ItemDto("LedgerEntryNotAtBank", tampered.ToString(), null, 2_500, Workloads.Currency));
        result.Mismatched.ShouldBe(1);

        // Payments stamps every matched payment of the run (PaymentsReconciled); the tampered top-up was not matched.
        foreach (var topUp in topUps.Skip(1))
            await Eventually.ShouldAsync(async () => (await ReconciledAtAsync(client, topUp.Id, ct)) is not null, StepTimeout,
                $"Top-up {topUp.Id} was not stamped reconciled.");
        (await ReconciledAtAsync(client, tampered, ct)).ShouldBeNull();

        await Eventually.ShouldAsync(() => MismatchMailArrivedAsync(today, tampered, ct), StepTimeout,
            $"No mismatch mail for {tampered} reached the operations mailbox in Mailpit.");
        await LedgerAssert.InvariantsHoldAsync(SystemInvariants.AccountsDatabase);
    }

    public async ValueTask DisposeAsync() => await stack.CollectLogsIfFailedAsync();

    private static async Task<DateTimeOffset?> ReconciledAtAsync(LedgerlineClient client, Guid paymentId, CancellationToken ct) =>
        (await client.GetAsync<ReconciledPaymentDto>(KeycloakTokens.Admin, $"/api/payments/{paymentId}", ct)).ReconciledAt;

    /// <summary>Mailpit (published on localhost:8025, MAILPIT_URL like scripts/smoke.sh) holds the operations mail of this mismatch.</summary>
    private static async Task<bool> MismatchMailArrivedAsync(DateOnly date, Guid reference, CancellationToken ct)
    {
        using var mailpit = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("MAILPIT_URL") ?? "http://localhost:8025") };
        // The volume keeps mail across runs, so the search is by this payment's reference, never by a count.
        var query = Uri.EscapeDataString($"to:{OperationsEmail} \"{reference}\"");
        var found = await mailpit.GetFromJsonAsync<JsonElement>($"/api/v1/search?query={query}", ct);
        var subject = $"Reconciliation mismatch on {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}: LedgerEntryNotAtBank";
        return found.GetProperty("messages").EnumerateArray().Any(message =>
            message.GetProperty("Subject").GetString() == subject &&
            message.GetProperty("To").EnumerateArray().Any(to => to.GetProperty("Address").GetString() == OperationsEmail));
    }

    private sealed record StatementLineDto(Guid JournalEntryId, string EntryType, string Direction, long AmountMinor, string Currency,
        long BalanceAfterMinor, Guid PaymentId, DateTimeOffset PostedAt);

    private sealed record RunDto(Guid RunId, DateOnly BusinessDate, string Status, int Matched, int Mismatched, bool InputsIncomplete,
        string? Error, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);

    private sealed record ItemDto(string Code, string Reference, long? BankAmountMinor, long? LedgerAmountMinor, string? Currency);

    private sealed record ReconciledPaymentDto(Guid Id, string Status, DateTimeOffset? ReconciledAt);
}
