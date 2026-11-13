using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Accounts.IntegrationTests;

public sealed class RefundTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Refund_moves_money_from_merchant_to_customer()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "refund");
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.#");
        var (customer, merchant) = await WalletsAsync(host);
        var funding = Guid.NewGuid();
        await probe.PublishAsync(new CreditTopUp(funding, Guid.NewGuid(), merchant, 1000, "TRY"), funding);
        await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == funding);
        var operation = Guid.NewGuid();
        await probe.PublishAsync(new RefundFunds(operation, Guid.NewGuid(), Guid.NewGuid(), merchant, customer, 250, "TRY"), operation);
        (await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == operation)).Kind.ShouldBe(LedgerOperationKind.RefundFunds);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        (await db.Accounts.SingleAsync(x => x.Id == customer, TestContext.Current.CancellationToken)).BalanceMinor.ShouldBe(250);
        (await db.Accounts.SingleAsync(x => x.Id == merchant, TestContext.Current.CancellationToken)).BalanceMinor.ShouldBe(750);
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    [Fact]
    public async Task Refund_with_insufficient_merchant_balance_is_rejected()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "refund_empty");
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.#");
        var (customer, merchant) = await WalletsAsync(host);
        var operation = Guid.NewGuid();
        await probe.PublishAsync(new RefundFunds(operation, Guid.NewGuid(), Guid.NewGuid(), merchant, customer, 250, "TRY"), operation);
        (await probe.WaitForAsync<LedgerOperationRejected>(x => x.OperationId == operation)).ReasonCode.ShouldBe("InsufficientFunds");
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    private static async Task<(Guid Customer, Guid Merchant)> WalletsAsync(ServiceHost<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        return (await TestAccounts.OpenWalletAsync(db, Guid.NewGuid(), "TRY"),
            await TestAccounts.OpenWalletAsync(db, Guid.NewGuid(), "TRY", "MerchantWallet"));
    }
}
