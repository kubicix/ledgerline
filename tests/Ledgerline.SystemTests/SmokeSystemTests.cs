using Ledgerline.SystemTests.Harness;

namespace Ledgerline.SystemTests;

[Collection(ComposeTests.Name)]
public sealed class SmokeSystemTests(ComposeStack stack) : IAsyncDisposable
{
    [Fact(Skip = ComposeStack.SkipReason, SkipUnless = nameof(ComposeStack.Enabled), SkipType = typeof(ComposeStack))]
    public async Task Stack_starts_and_smoke_flow_passes()
    {
        var ct = TestContext.Current.CancellationToken;
        await stack.UpAsync(ct);
        await stack.EnsureVelocityRaisedAsync(ct);
        using var client = LedgerlineClient.CreateDefault();
        var wallets = await Workloads.EnsureSmokeWalletsAsync(client, ct);
        var run = await SystemRun.StartAsync(client, wallets.All, ct);

        await Workloads.SmokeFlowAsync(client, run, wallets, ct);

        var deltas = await run.WalletDeltasAsync(client, ct);
        deltas[wallets.Alice.AccountId].ShouldBe(6000);
        deltas[wallets.Bob.AccountId].ShouldBe(2500);
        deltas[wallets.Shop.AccountId].ShouldBe(1500);
        await SystemInvariants.AssertAsync(client, run, ct: ct);
    }

    public async ValueTask DisposeAsync() => await stack.CollectLogsIfFailedAsync();
}
