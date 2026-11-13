using System.Net;
using System.Net.Http.Json;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Bank;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Ledgerline.Payments.Sagas;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using BankProgram = Ledgerline.BankSimulator.Program;
using BankDb = Ledgerline.BankSimulator.Infrastructure.BankDbContext;
using SimulatorOptions = Ledgerline.BankSimulator.Features.BankOptions;

namespace Ledgerline.Payments.IntegrationTests;

public sealed class BankCallTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Authorize_and_capture_drive_saga_to_LedgerCredit()
    {
        await using var scenario = await SetupAsync();
        var payment = await scenario.StartAsync();
        await scenario.Probe.WaitForAsync<CreditTopUp>(x => x.PaymentId == payment);
        var stored = await scenario.PaymentAsync(payment);
        stored.SagaStep.ShouldBe(SagaStep.LedgerCredit);
        stored.BankAuthorizationId.ShouldNotBeNullOrWhiteSpace();
        stored.BankCaptureId.ShouldNotBeNullOrWhiteSpace();
        stored.CardToken.ShouldBeNull();
    }

    [Fact]
    public async Task Declined_authorization_fails_payment()
    {
        await using var scenario = await SetupAsync();
        var payment = await scenario.StartAsync("tok_declined");
        var failed = await scenario.Probe.WaitForAsync<PaymentFailed>(x => x.PaymentId == payment);
        failed.ReasonCode.ShouldBe("BankDeclined:do_not_honor");
        (await scenario.PaymentAsync(payment)).Status.ShouldBe(PaymentStatus.Failed);
        (await scenario.PaymentAsync(payment)).CardToken.ShouldBeNull();
    }

    [Fact]
    public async Task Bank_timeout_with_success_is_resolved_by_lookup()
    {
        await using var scenario = await SetupAsync();
        await scenario.SetFaultAsync("SuccessThenTimeout");
        var payment = await scenario.StartAsync();
        await scenario.Probe.WaitForAsync<CreditTopUp>(x => x.PaymentId == payment);
        (await scenario.BankOperationsAsync("Authorize")).ShouldBe(1);
        (await scenario.BankOperationsAsync("Capture")).ShouldBe(1);
    }

    [Fact]
    public async Task Bank_timeout_without_effect_is_retried_and_succeeds()
    {
        await using var scenario = await SetupAsync();
        await scenario.SetFaultAsync("Timeout");
        var payment = await scenario.StartAsync();
        await scenario.Probe.WaitForAsync<CreditTopUp>(x => x.PaymentId == payment);
        (await scenario.BankOperationsAsync("Authorize")).ShouldBe(1);
        (await scenario.BankOperationsAsync("Capture")).ShouldBe(1);
    }

    [Fact]
    public async Task Bank_error_500_is_retried()
    {
        await using var scenario = await SetupAsync();
        await scenario.SetFaultAsync("Error500");
        var payment = await scenario.StartAsync();
        await scenario.Probe.WaitForAsync<CreditTopUp>(x => x.PaymentId == payment);
        (await scenario.BankOperationsAsync("Authorize")).ShouldBe(1);
    }

    [Fact]
    public async Task Crash_after_bank_capture_before_commit_does_not_capture_twice()
    {
        CaptureCrashPoint? crash = null;
        await using var scenario = await SetupAsync(services => services.AddSingleton<ICrashPoint>(provider =>
            crash = new CaptureCrashPoint(provider.GetRequiredService<IHostApplicationLifetime>())));
        var payment = await scenario.StartAsync();
        await crash!.Crashed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var cs = scenario.Host.ConnectionString;
        await scenario.Host.DisposeAsync();
        await using var restarted = await ServiceHost<Program>.StartOnDatabaseAsync(infrastructure, cs, scenario.Configure);
        await scenario.Probe.WaitForAsync<CreditTopUp>(x => x.PaymentId == payment);
        (await scenario.BankOperationsAsync("Capture")).ShouldBe(1);
        await using var scope = restarted.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Payments.SingleAsync(x => x.Id == payment, TestContext.Current.CancellationToken);
        stored.BankCaptureId.ShouldNotBeNullOrWhiteSpace();
        stored.SagaStep.ShouldBe(SagaStep.LedgerCredit);
    }

    [Fact]
    public async Task Stale_call_bank_command_for_other_step_is_ignored()
    {
        await using var scenario = await SetupAsync();
        var id = Guid.NewGuid();
        await using (var scope = scenario.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            db.Payments.Add(new Payment { Id = id, Kind = PaymentKind.TopUp, Status = PaymentStatus.Processing,
                SagaStep = SagaStep.LedgerCredit, CustomerId = Guid.NewGuid(), DestinationAccountId = Guid.NewGuid(),
                AmountMinor = 2500, Currency = "TRY", IdempotencyKey = "stale", RequestHash = "synthetic", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var message = Guid.NewGuid();
        await scenario.Probe.PublishAsync(new CallBank(id, "Capture"), id, message);
        await Eventually.ShouldAsync(async () =>
        {
            await using var scope = scenario.Host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<InboxMessage>().AnyAsync(x => x.MessageId == message);
        });
        (await scenario.BankOperationsAsync("Authorize")).ShouldBe(0);
        (await scenario.BankOperationsAsync("Capture")).ShouldBe(0);
    }

    private async Task<Scenario> SetupAsync(Action<IServiceCollection>? overrides = null)
    {
        var key = Guid.NewGuid().ToString("N");
        var bank = await ServiceHost<BankProgram>.StartAsync(infrastructure, "bank_calls",
            services => services.Configure<SimulatorOptions>(options => options.ApiKey = key));
        var client = bank.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        client.Timeout = TimeSpan.FromMilliseconds(500);
        void Configure(IServiceCollection services)
        {
            services.AddSingleton<IBankClient>(new BankClient(client));
            services.AddSingleton(new PaymentSagaOptions { BankEnabled = true, RiskEnabled = false });
            services.AddSingleton(new ConsumerOptions { Service = "payments", RetryTtlMs = 100, MaxAttempts = 5 });
        }
        var host = await ServiceHost<Program>.StartAsync(infrastructure, "bank_payment", services =>
        {
            Configure(services);
            overrides?.Invoke(services);
        });
        var probe = await PaymentTestSetup.ProbeAsync(host);
        return new(host, bank, client, probe, Configure);
    }

    public sealed class CaptureCrashPoint(IHostApplicationLifetime lifetime) : ICrashPoint
    {
        private int responses;
        private readonly TaskCompletionSource crashed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Crashed => crashed.Task;
        public void Hit(string name)
        {
            if (name != "bank.after-response" || Interlocked.Increment(ref responses) != 2) return;
            lifetime.StopApplication();
            crashed.TrySetResult();
            throw new HostAbortedException();
        }
    }

    private sealed class Scenario(ServiceHost<Program> host, ServiceHost<BankProgram> bank, HttpClient client,
        BrokerProbe probe, Action<IServiceCollection> configure) : IAsyncDisposable
    {
        public ServiceHost<Program> Host => host;
        public BrokerProbe Probe => probe;
        public Action<IServiceCollection> Configure => configure;
        public async Task<Guid> StartAsync(string token = "tok_ok")
        {
            await using var scope = host.Services.CreateAsyncScope();
            var id = Guid.NewGuid();
            await scope.ServiceProvider.GetRequiredService<SagaRunner>().StartAsync(new Payment
            {
                Id = id, Kind = PaymentKind.TopUp, Status = PaymentStatus.Processing, CustomerId = Guid.NewGuid(),
                DestinationAccountId = Guid.NewGuid(), AmountMinor = 2500, Currency = "TRY", CardToken = token,
                IdempotencyKey = id.ToString("N"), RequestHash = "synthetic", CreatedAt = DateTimeOffset.UtcNow
            }, TestContext.Current.CancellationToken);
            return id;
        }
        public async Task SetFaultAsync(string mode)
        {
            using var response = await client.PutAsJsonAsync("/faults", new { operation = "Authorize", mode, count = 1, delayMs = 2000 }, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
        public async Task<int> BankOperationsAsync(string kind)
        {
            await using var scope = bank.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<BankDb>().Operations.CountAsync(x => x.Kind == kind, TestContext.Current.CancellationToken);
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
            client.Dispose();
            await bank.DisposeAsync();
        }
    }
}
