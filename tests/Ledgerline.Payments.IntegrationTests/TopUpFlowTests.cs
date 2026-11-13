using System.Net;
using System.Net.Http.Json;
using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Contracts.Risk;
using Ledgerline.Payments.Bank;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Ledgerline.Payments.Sagas;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BankProgram = Ledgerline.BankSimulator.Program;
using BankDb = Ledgerline.BankSimulator.Infrastructure.BankDbContext;
using SimulatorOptions = Ledgerline.BankSimulator.Features.BankOptions;

namespace Ledgerline.Payments.IntegrationTests;

public sealed class TopUpFlowTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task TopUp_happy_path_completes_and_clears_card_token()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.CreateAsync();
        await scenario.ApproveAsync(id);
        var credit = await scenario.Probe.WaitForAsync<CreditTopUp>(x => x.PaymentId == id);
        await scenario.Probe.PublishAsync(new LedgerOperationSucceeded(credit.OperationId, id, LedgerOperationKind.CreditTopUp,
            Guid.NewGuid(), DateTimeOffset.UtcNow), id);
        var completed = await scenario.Probe.WaitForAsync<PaymentCompleted>(x => x.PaymentId == id);
        var stored = await scenario.PaymentAsync(id);
        stored.Status.ShouldBe(PaymentStatus.Completed);
        stored.CardToken.ShouldBeNull();
        completed.BankCaptureId.ShouldBe(stored.BankCaptureId);
    }

    [Fact]
    public async Task Risk_rejection_ends_Rejected_without_bank_call()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.CreateAsync();
        await scenario.Probe.WaitForAsync<EvaluateRisk>(x => x.PaymentId == id);
        await scenario.Probe.PublishAsync(new RiskRejected(id, ["DailyAmountLimit"], DateTimeOffset.UtcNow), id);
        (await scenario.Probe.WaitForAsync<PaymentFailed>(x => x.PaymentId == id)).Status.ShouldBe("Rejected");
        (await scenario.BankCountAsync()).ShouldBe(0);
        (await scenario.PaymentAsync(id)).CardToken.ShouldBeNull();
    }

    [Fact]
    public async Task Ledger_rejection_after_bank_capture_refunds_at_bank()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.CreateAsync();
        await scenario.ApproveAsync(id);
        await scenario.RejectCreditAsync(id);
        var failed = await scenario.Probe.WaitForAsync<PaymentFailed>(x => x.PaymentId == id);
        failed.Status.ShouldBe("Compensated");
        failed.ReasonCode.ShouldBe("AccountFrozen");
        (await scenario.BankCountAsync("Capture")).ShouldBe(1);
        (await scenario.BankCountAsync("Refund")).ShouldBe(1);
    }

    [Fact]
    public async Task Capture_declined_voids_authorization_and_ends_Compensated()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.CreateAsync("tok_capture_fails");
        await scenario.ApproveAsync(id);
        (await scenario.Probe.WaitForAsync<PaymentFailed>(x => x.PaymentId == id)).Status.ShouldBe("Compensated");
        (await scenario.BankCountAsync("Void")).ShouldBe(1);
        await using var scope = scenario.Bank.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<BankDb>().Authorizations.SingleAsync(TestContext.Current.CancellationToken)).Status.ShouldBe("Voided");
    }

    [Fact]
    public async Task Exhausted_compensation_ends_in_manual_review()
    {
        await using var scenario = await SetupAsync();
        using var injected = await scenario.BankClient.PutAsJsonAsync("/faults", new { operation = "Refund", mode = "Error500", count = 100 }, TestContext.Current.CancellationToken);
        injected.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var id = await scenario.CreateAsync();
        await scenario.ApproveAsync(id);
        await scenario.RejectCreditAsync(id);
        (await scenario.Probe.WaitForAsync<PaymentFailed>(x => x.PaymentId == id)).Status.ShouldBe("ManualReview");
        var payment = await scenario.PaymentAsync(id);
        payment.StepAttempt.ShouldBe(8);
        payment.FailureReason.ShouldBe("AccountFrozen");
        (await scenario.BankCountAsync("Refund")).ShouldBe(0);
    }

    [Fact]
    public async Task Frozen_account_is_rejected_synchronously_when_replica_knows()
    {
        await using var scenario = await SetupAsync();
        await scenario.Probe.PublishAsync(new AccountStatusChanged(scenario.Wallet, "Frozen", DateTimeOffset.UtcNow), scenario.Wallet);
        await WaitForFrozenAsync(scenario.Host, scenario.Wallet);
        using var customer = scenario.Host.CreateClient(scenario.Owner);
        using var request = Request(scenario.Wallet, "tok_ok");
        using var response = await customer.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await scenario.BankCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Status_event_before_account_opened_waits_for_replica()
    {
        await using var scenario = await SetupAsync();
        var wallet = Guid.NewGuid();
        await scenario.Probe.PublishAsync(new AccountStatusChanged(wallet, "Frozen", DateTimeOffset.UtcNow), wallet);
        await scenario.Probe.PublishAsync(new AccountOpened(wallet, scenario.Owner, "CustomerWallet", "TRY", null, DateTimeOffset.UtcNow), wallet);
        await WaitForFrozenAsync(scenario.Host, wallet);
    }

    private async Task<Scenario> SetupAsync()
    {
        var key = Guid.NewGuid().ToString("N");
        var bank = await ServiceHost<BankProgram>.StartAsync(infrastructure, "topup_bank",
            services => services.Configure<SimulatorOptions>(options => options.ApiKey = key));
        var client = bank.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        var host = await ServiceHost<Program>.StartAsync(infrastructure, "topup_flow", services =>
        {
            services.AddSingleton<IBankClient>(new BankClient(client));
            services.AddSingleton(new PaymentSagaOptions { RiskEnabled = true, BankEnabled = true });
            services.AddSingleton(new TimeoutOptions { CompensationTimeoutMs = 50, PollIntervalMs = 20 });
            services.AddSingleton(new ConsumerOptions { Service = "payments", RetryTtlMs = 100, MaxAttempts = 5 });
        });
        var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri,
            "accounts.command.#", "risk.command.#", "payments.event.#");
        var owner = Guid.NewGuid();
        var wallet = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        return new(host, bank, client, probe, owner, wallet);
    }

    private static HttpRequestMessage Request(Guid wallet, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/topups")
        {
            Content = JsonContent.Create(new { accountId = wallet, amountMinor = 2500, currency = "TRY", cardToken = token })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return request;
    }

    private static Task WaitForFrozenAsync(ServiceHost<Program> host, Guid wallet) => Eventually.ShouldAsync(async () =>
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().AccountRefs.AnyAsync(x => x.AccountId == wallet && x.Status == "Frozen");
    });

    private sealed class Scenario(ServiceHost<Program> host, ServiceHost<BankProgram> bank, HttpClient bankClient,
        BrokerProbe probe, Guid owner, Guid wallet) : IAsyncDisposable
    {
        public ServiceHost<Program> Host => host;
        public ServiceHost<BankProgram> Bank => bank;
        public HttpClient BankClient => bankClient;
        public BrokerProbe Probe => probe;
        public Guid Owner => owner;
        public Guid Wallet => wallet;
        public async Task<Guid> CreateAsync(string token = "tok_ok")
        {
            using var customer = host.CreateClient(owner);
            using var request = Request(wallet, token);
            using var response = await customer.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            return (await PaymentTestSetup.BodyAsync(response)).GetProperty("id").GetGuid();
        }
        public async Task ApproveAsync(Guid id)
        {
            await probe.WaitForAsync<EvaluateRisk>(x => x.PaymentId == id);
            await probe.PublishAsync(new RiskApproved(id, DateTimeOffset.UtcNow), id);
        }
        public async Task RejectCreditAsync(Guid id)
        {
            var credit = await probe.WaitForAsync<CreditTopUp>(x => x.PaymentId == id);
            await probe.PublishAsync(new LedgerOperationRejected(credit.OperationId, id, LedgerOperationKind.CreditTopUp, "AccountFrozen"), id);
        }
        public async Task<int> BankCountAsync(string? kind = null)
        {
            await using var scope = bank.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<BankDb>().Operations.CountAsync(x => kind == null || x.Kind == kind, TestContext.Current.CancellationToken);
        }
        public async Task<Payment> PaymentAsync(Guid id)
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Payments.AsNoTracking().SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            await probe.DisposeAsync();
            await host.DisposeAsync();
            bankClient.Dispose();
            await bank.DisposeAsync();
        }
    }
}
