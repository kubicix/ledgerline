using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Testing;
using Ledgerline.Testing.Faults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledgerline.Accounts.IntegrationTests;

public sealed class CrashTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Crash_after_ledger_commit_before_ack_does_not_double_post()
    {
        var crashed = await ServiceHost<Program>.StartAsync(infrastructure, "ledger_crash", services =>
            services.AddSingleton<ICrashPoint>(provider => new StoppingCrashPoint("ledger.after-commit", provider.GetRequiredService<IHostApplicationLifetime>())));
        var cs = crashed.ConnectionString;
        Guid wallet;
        await using (var scope = crashed.Services.CreateAsyncScope())
            wallet = await TestAccounts.OpenWalletAsync(scope.ServiceProvider.GetRequiredService<AccountsDbContext>(), Guid.NewGuid(), "TRY");
        await using var probe = await BrokerProbe.CreateAsync(crashed.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.#");
        var payment = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        try
        {
            await probe.PublishAsync(new CreditTopUp(operation, payment, wallet, 1000, "TRY"), payment, messageId);
            await ((StoppingCrashPoint)crashed.Services.GetRequiredService<ICrashPoint>()).Crashed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally { await crashed.DisposeAsync(); }
        var acknowledgments = new LedgerCommandTests.CountingAck();
        await using var restarted = await ServiceHost<Program>.StartOnDatabaseAsync(infrastructure, cs,
            services => services.AddSingleton<IAckStrategy>(acknowledgments));
        await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == operation);
        await Eventually.ShouldAsync(() => Task.FromResult(acknowledgments.Count == 1));
        await using var verification = restarted.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<AccountsDbContext>();
        (await db.JournalEntries.CountAsync(x => x.OperationId == operation, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.Accounts.SingleAsync(x => x.Id == wallet, TestContext.Current.CancellationToken)).BalanceMinor.ShouldBe(1000);
        probe.Messages.Count(x => x.Type == "accounts.event.ledger-operation-succeeded.v1").ShouldBe(1);
        await LedgerAssert.InvariantsHoldAsync(cs);
    }
}
