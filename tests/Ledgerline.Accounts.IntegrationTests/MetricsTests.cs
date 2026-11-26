using System.Diagnostics.Metrics;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Accounts.IntegrationTests;

/// <summary>The recorder listens to this host's meter factory only, so parallel Accounts tests do not mix in.</summary>
public sealed class MetricsTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Ledger_imbalance_gauge_is_zero_after_postings()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "metrics",
            services => services.AddSingleton(new LedgerMetricsOptions { RefreshInterval = TimeSpan.FromMilliseconds(100) }));
        using var metrics = MetricRecorder.ForMeter("Ledgerline", host.Services.GetRequiredService<IMeterFactory>());
        Guid from, to;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
            from = await TestAccounts.OpenWalletAsync(db, Ids.New(), "TRY");
            to = await TestAccounts.OpenWalletAsync(db, Ids.New(), "TRY");
        }
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.#");
        var topUp = Ids.New();
        var transfer = Ids.New();
        await probe.PublishAsync(new CreditTopUp(topUp, Ids.New(), from, 1000, "TRY"), Ids.New());
        await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == topUp);
        await probe.PublishAsync(new TransferFunds(transfer, Ids.New(), from, to, 250, "TRY"), Ids.New());
        await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == transfer);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
            (await db.Accounts.SingleAsync(x => x.Id == from, TestContext.Current.CancellationToken)).BalanceMinor.ShouldBe(750);
            (await db.Accounts.SingleAsync(x => x.SystemCode == "BANK_CLEARING" && x.Currency == "TRY", TestContext.Current.CancellationToken))
                .BalanceMinor.ShouldBe(-1000);
        }

        // A refresh running when the postings finished may have read older balances; two more completions guarantee
        // that one whole refresh started after the postings, so the cached trial balance includes them.
        var ledgerMetrics = host.Services.GetRequiredService<LedgerMetrics>();
        var mark = ledgerMetrics.Refreshes;
        await Eventually.ShouldAsync(() => Task.FromResult(ledgerMetrics.Refreshes >= mark + 2),
            because: "Expected the trial balance to be refreshed after the postings");
        var before = metrics.Of("ledgerline.ledger.imbalance").Length;
        metrics.RecordObservableInstruments();
        var after = metrics.Of("ledgerline.ledger.imbalance").Skip(before).ToArray();
        after.ShouldContain(m => Equals(m.Tag("currency"), "TRY"));
        after.ShouldAllBe(m => (long)m.Value == 0L);
        var postings = metrics.Of("ledgerline.ledger.posting.duration");
        postings.Length.ShouldBeGreaterThanOrEqualTo(2);
        postings.ShouldAllBe(m => Equals(m.Tag("outcome"), "Succeeded"));
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }
}
