using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Accounts.IntegrationTests;

public sealed class HoldTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Reserve_moves_funds_to_pending_and_creates_active_hold()
    {
        await using var scenario = await SetupAsync();
        var payment = await scenario.ReserveAsync();
        (await scenario.HoldAsync(payment)).Status.ShouldBe("Active");
        (await scenario.BalanceAsync(scenario.Customer)).ShouldBe(7500);
        using var admin = scenario.Host.CreateClient(Guid.NewGuid(), "admin");
        var account = await admin.GetFromJsonAsync<JsonElement>($"/accounts/{scenario.Customer}", TestContext.Current.CancellationToken);
        account.GetProperty("heldMinor").GetInt64().ShouldBe(2500);
        await RebuildProjectionsCommand.RunAsync(scenario.Host.ConnectionString, TestContext.Current.CancellationToken);
        (await scenario.HoldAsync(payment)).Status.ShouldBe("Active");
        await LedgerAssert.InvariantsHoldAsync(scenario.Host.ConnectionString);
    }

    [Fact]
    public async Task Reserve_with_insufficient_funds_is_rejected()
    {
        await using var scenario = await SetupAsync(0);
        var payment = Guid.NewGuid();
        var operation = Guid.NewGuid();
        await scenario.Probe.PublishAsync(new ReserveFunds(operation, payment, scenario.Customer, 2500, "TRY"), payment);
        (await scenario.Probe.WaitForAsync<LedgerOperationRejected>(x => x.OperationId == operation)).ReasonCode.ShouldBe("InsufficientFunds");
        await LedgerAssert.InvariantsHoldAsync(scenario.Host.ConnectionString);
    }

    [Fact]
    public async Task Second_reserve_for_same_payment_is_rejected_HoldNotActive()
    {
        await using var scenario = await SetupAsync();
        var payment = await scenario.ReserveAsync();
        var operation = Guid.NewGuid();
        await scenario.Probe.PublishAsync(new ReserveFunds(operation, payment, scenario.Customer, 2500, "TRY"), payment);
        (await scenario.Probe.WaitForAsync<LedgerOperationRejected>(x => x.OperationId == operation)).ReasonCode.ShouldBe("HoldNotActive");
        await LedgerAssert.InvariantsHoldAsync(scenario.Host.ConnectionString);
    }

    [Fact]
    public async Task Full_capture_pays_merchant_and_marks_hold_Captured()
    {
        await using var scenario = await SetupAsync();
        var payment = await scenario.ReserveAsync();
        await scenario.CaptureAsync(payment, 2500);
        (await scenario.BalanceAsync(scenario.Merchant)).ShouldBe(2500);
        (await scenario.HoldAsync(payment)).Status.ShouldBe("Captured");
        await RebuildProjectionsCommand.RunAsync(scenario.Host.ConnectionString, TestContext.Current.CancellationToken);
        (await scenario.HoldAsync(payment)).Status.ShouldBe("Captured");
        await LedgerAssert.InvariantsHoldAsync(scenario.Host.ConnectionString);
    }

    [Fact]
    public async Task Partial_capture_returns_remainder_to_customer()
    {
        await using var scenario = await SetupAsync();
        var payment = await scenario.ReserveAsync();
        await scenario.CaptureAsync(payment, 1000);
        (await scenario.BalanceAsync(scenario.Customer)).ShouldBe(9000);
        (await scenario.BalanceAsync(scenario.Merchant)).ShouldBe(1000);
        await LedgerAssert.InvariantsHoldAsync(scenario.Host.ConnectionString);
    }

    [Fact]
    public async Task Capture_above_hold_is_rejected_AmountExceedsHold()
    {
        await using var scenario = await SetupAsync();
        var payment = await scenario.ReserveAsync();
        await AssertCaptureRejectedAsync(scenario, payment, 2501, "AmountExceedsHold");
        await LedgerAssert.InvariantsHoldAsync(scenario.Host.ConnectionString);
    }

    [Fact]
    public async Task Capture_without_hold_is_rejected_HoldNotFound()
    {
        await using var scenario = await SetupAsync();
        await AssertCaptureRejectedAsync(scenario, Guid.NewGuid(), 1000, "HoldNotFound");
        await LedgerAssert.InvariantsHoldAsync(scenario.Host.ConnectionString);
    }

    [Fact]
    public async Task Release_returns_funds_and_marks_hold_Released()
    {
        await using var scenario = await SetupAsync();
        var payment = await scenario.ReserveAsync();
        await scenario.ReleaseAsync(payment);
        (await scenario.BalanceAsync(scenario.Customer)).ShouldBe(10000);
        await RebuildProjectionsCommand.RunAsync(scenario.Host.ConnectionString, TestContext.Current.CancellationToken);
        (await scenario.HoldAsync(payment)).Status.ShouldBe("Released");
        await LedgerAssert.InvariantsHoldAsync(scenario.Host.ConnectionString);
    }

    [Fact]
    public async Task Release_succeeds_when_customer_account_is_frozen()
    {
        await using var scenario = await SetupAsync();
        var payment = await scenario.ReserveAsync();
        await using (var scope = scenario.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
            var customer = await db.Accounts.SingleAsync(x => x.Id == scenario.Customer, TestContext.Current.CancellationToken);
            customer.Status = "Frozen";
            customer.Version++;
            db.Events.Add(new LedgerEvent { StreamId = customer.Id, Version = customer.Version, Type = "account-status-changed.v1",
                Data = EnvelopeSerializer.ToPayload(new { Status = "Frozen" }), Metadata = EnvelopeSerializer.ToPayload(new { CorrelationId = payment }), OccurredAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await scenario.ReleaseAsync(payment);
        (await scenario.BalanceAsync(scenario.Customer)).ShouldBe(10000);
        await LedgerAssert.InvariantsHoldAsync(scenario.Host.ConnectionString);
    }

    [Fact]
    public async Task Capture_after_release_is_rejected_HoldNotActive()
    {
        await using var scenario = await SetupAsync();
        var payment = await scenario.ReserveAsync();
        await scenario.ReleaseAsync(payment);
        await AssertCaptureRejectedAsync(scenario, payment, 1000, "HoldNotActive");
        await LedgerAssert.InvariantsHoldAsync(scenario.Host.ConnectionString);
    }

    [Fact]
    public async Task Duplicate_reserve_then_capture_commands_keep_hold_consistent()
    {
        await using var scenario = await SetupAsync();
        var payment = Guid.NewGuid();
        var reserve = new ReserveFunds(Guid.NewGuid(), payment, scenario.Customer, 2500, "TRY");
        await scenario.Probe.PublishAsync(reserve, payment);
        await scenario.Probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == reserve.OperationId);
        await scenario.Probe.PublishAsync(reserve, payment);
        var capture = new CaptureFunds(Guid.NewGuid(), payment, scenario.Merchant, 1000);
        await scenario.Probe.PublishAsync(capture, payment);
        await scenario.Probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == capture.OperationId);
        await scenario.Probe.PublishAsync(capture, payment);
        await Eventually.ShouldAsync(() => Task.FromResult(scenario.Probe.Messages.Count(x => x.Type == "accounts.event.ledger-operation-succeeded.v1") == 5));
        (await scenario.HoldAsync(payment)).Status.ShouldBe("Captured");
        (await scenario.BalanceAsync(scenario.Customer)).ShouldBe(9000);
        (await scenario.BalanceAsync(scenario.Merchant)).ShouldBe(1000);
        await LedgerAssert.InvariantsHoldAsync(scenario.Host.ConnectionString);
    }

    [Fact]
    public async Task Concurrent_capture_and_release_have_one_winner()
    {
        await using var scenario = await SetupAsync();
        await using var replica = await ServiceHost<Program>.StartOnDatabaseAsync(infrastructure, scenario.Host.ConnectionString);
        var payments = new List<Guid>();
        for (var i = 0; i < 50; i++) payments.Add(await scenario.ReserveAsync(100));
        foreach (var payment in payments)
            await Task.WhenAll(scenario.Probe.PublishAsync(new CaptureFunds(Guid.NewGuid(), payment, scenario.Merchant, 100), payment),
                scenario.Probe.PublishAsync(new ReleaseFunds(Guid.NewGuid(), payment), payment));
        await Eventually.ShouldAsync(async () =>
        {
            await using var scope = scenario.Host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AccountsDbContext>().LedgerOperations.CountAsync() == 151;
        }, TimeSpan.FromSeconds(30));
        await using var scope = scenario.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        var results = await db.LedgerOperations.Where(x => x.Kind == "CaptureFunds" || x.Kind == "ReleaseFunds").ToListAsync(TestContext.Current.CancellationToken);
        foreach (var payment in payments)
        {
            results.Count(x => x.PaymentId == payment && x.Outcome == "Succeeded").ShouldBe(1);
            results.Single(x => x.PaymentId == payment && x.Outcome == "Rejected").ReasonCode.ShouldBe("HoldNotActive");
        }
        await LedgerAssert.InvariantsHoldAsync(scenario.Host.ConnectionString);
    }

    private static async Task AssertCaptureRejectedAsync(Scenario scenario, Guid payment, long amount, string reason)
    {
        var operation = Guid.NewGuid();
        await scenario.Probe.PublishAsync(new CaptureFunds(operation, payment, scenario.Merchant, amount), payment);
        (await scenario.Probe.WaitForAsync<LedgerOperationRejected>(x => x.OperationId == operation)).ReasonCode.ShouldBe(reason);
    }

    private async Task<Scenario> SetupAsync(long funding = 10000)
    {
        var host = await ServiceHost<Program>.StartAsync(infrastructure, "holds");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        var customer = await TestAccounts.OpenWalletAsync(db, Guid.NewGuid(), "TRY");
        var merchant = await TestAccounts.OpenWalletAsync(db, Guid.NewGuid(), "TRY", "MerchantWallet");
        var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.#");
        if (funding > 0)
        {
            var operation = Guid.NewGuid();
            await probe.PublishAsync(new CreditTopUp(operation, Guid.NewGuid(), customer, funding, "TRY"), operation);
            await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == operation);
        }
        return new(host, probe, customer, merchant);
    }

    private sealed class Scenario(ServiceHost<Program> host, BrokerProbe probe, Guid customer, Guid merchant) : IAsyncDisposable
    {
        public ServiceHost<Program> Host => host;
        public BrokerProbe Probe => probe;
        public Guid Customer => customer;
        public Guid Merchant => merchant;
        public async Task<Guid> ReserveAsync(long amount = 2500)
        {
            var payment = Guid.NewGuid();
            var operation = Guid.NewGuid();
            await probe.PublishAsync(new ReserveFunds(operation, payment, customer, amount, "TRY"), payment);
            await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == operation);
            return payment;
        }
        public async Task CaptureAsync(Guid payment, long amount)
        {
            var operation = Guid.NewGuid();
            await probe.PublishAsync(new CaptureFunds(operation, payment, merchant, amount), payment);
            await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == operation);
        }
        public async Task ReleaseAsync(Guid payment)
        {
            var operation = Guid.NewGuid();
            await probe.PublishAsync(new ReleaseFunds(operation, payment), payment);
            await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == operation);
        }
        public async Task<Hold> HoldAsync(Guid payment)
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AccountsDbContext>().Holds.AsNoTracking().SingleAsync(x => x.PaymentId == payment, TestContext.Current.CancellationToken);
        }
        public async Task<long> BalanceAsync(Guid account)
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AccountsDbContext>().Accounts.Where(x => x.Id == account).Select(x => x.BalanceMinor).SingleAsync(TestContext.Current.CancellationToken);
        }
        public async ValueTask DisposeAsync() { await probe.DisposeAsync(); await host.DisposeAsync(); }
    }
}
