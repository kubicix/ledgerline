using System.Net;
using System.Net.Http.Json;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Payments;
using Ledgerline.Contracts.Risk;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Ledgerline.Payments.Sagas;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AccountsProgram = Ledgerline.Accounts.Program;
using AccountsDb = Ledgerline.Accounts.Infrastructure.AccountsDbContext;

namespace Ledgerline.Payments.IntegrationTests;

internal sealed class MerchantScenario(ServiceHost<AccountsProgram> accounts, ServiceHost<Program> host, BrokerProbe probe,
    Guid owner, Guid merchantOwner, Guid customer, Guid merchant) : IAsyncDisposable
{
    public ServiceHost<AccountsProgram> Accounts => accounts;
    public ServiceHost<Program> Host => host;
    public BrokerProbe Probe => probe;
    public Guid MerchantOwner => merchantOwner;
    public Guid Merchant => merchant;
    public Guid Owner => owner;
    public Guid Customer => customer;
    public async Task<Guid> CreateAsync(string mode = "Manual")
    {
        using var client = host.CreateClient(owner);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new { customerAccountId = customer, merchantAccountId = merchant,
                amountMinor = 2500, currency = "TRY", captureMode = mode })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await PaymentTestSetup.BodyAsync(response)).GetProperty("id").GetGuid();
    }
    public async Task ApproveAsync(Guid id)
    {
        await probe.WaitForAsync<EvaluateRisk>(x => x.PaymentId == id);
        await probe.PublishAsync(new RiskApproved(id, DateTimeOffset.UtcNow), id);
    }
    public async Task<Guid> AuthorizeAsync()
    {
        var id = await CreateAsync();
        await ApproveAsync(id);
        await probe.WaitForAsync<PaymentAuthorized>(x => x.PaymentId == id);
        return id;
    }
    public async Task<HttpResponseMessage> ActionAsync(Guid id, string action, long? amount = null, string? key = null)
    {
        using var client = host.CreateClient(merchantOwner, "merchant");
        using var request = ActionRequest(id, action, amount, key ?? Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
    public async Task SetMerchantStatusAsync(string action)
    {
        using var client = accounts.CreateClient(Guid.NewGuid(), "admin");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/accounts/{merchant}/{action}");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
    public async Task<Payment> PaymentAsync(Guid id)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Payments.AsNoTracking().SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
    }
    public async ValueTask DisposeAsync() { await probe.DisposeAsync(); await host.DisposeAsync(); await accounts.DisposeAsync(); }

    internal static async Task<MerchantScenario> StartAsync(InfrastructureFixture infrastructure, TimeSpan? ttl = null, Action<IServiceCollection>? accountOverrides = null, Action<IServiceCollection>? paymentOverrides = null)
    {
        var accounts = await ServiceHost<AccountsProgram>.StartAsync(infrastructure, "merchant_accounts", accountOverrides);
        var uri = accounts.Services.GetRequiredService<RabbitMqOptions>().Uri;
        var host = await ServiceHost<Program>.StartAsync(infrastructure, "merchant_payments", services =>
        {
            services.AddSingleton(new RabbitMqOptions { Uri = uri });
            services.AddSingleton(new PaymentSagaOptions { RiskEnabled = true, BankEnabled = false, AuthorizationTtl = ttl ?? TimeSpan.FromDays(7) });
            services.AddSingleton(new TimeoutOptions { PollIntervalMs = 20 });
            paymentOverrides?.Invoke(services);
        });
        var probe = await BrokerProbe.CreateAsync(uri, "accounts.event.#", "risk.command.#", "payments.event.#");
        var owner = Guid.NewGuid();
        var merchantOwner = Guid.NewGuid();
        var customer = await OpenAsync(accounts, owner, false);
        var merchant = await OpenAsync(accounts, merchantOwner, true);
        await Eventually.ShouldAsync(async () =>
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().AccountRefs.CountAsync() == 2;
        });
        await FundAsync(accounts, customer);
        return new(accounts, host, probe, owner, merchantOwner, customer, merchant);
    }

    private static async Task FundAsync(ServiceHost<AccountsProgram> accounts, Guid customer)
    {
        await using var scope = accounts.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDb>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var request = Ledgerline.Accounts.Ledger.Entries.TopUp(Guid.NewGuid(), Guid.NewGuid(), customer, Money.Create(10000, "TRY"));
        (await scope.ServiceProvider.GetRequiredService<Ledgerline.Accounts.Ledger.ILedger>().PostAsync(request, TestContext.Current.CancellationToken))
            .ShouldBeOfType<Ledgerline.Accounts.Ledger.PostingResult.Posted>();
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> OpenAsync(ServiceHost<AccountsProgram> accounts, Guid owner, bool merchant)
    {
        using var client = accounts.CreateClient(owner, merchant ? "merchant" : "customer");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/accounts") { Content = JsonContent.Create(new { type = merchant ? "MerchantWallet" : "CustomerWallet", currency = "TRY" }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await PaymentTestSetup.BodyAsync(response)).GetProperty("id").GetGuid();
    }

    internal static HttpRequestMessage ActionRequest(Guid id, string action, long? amount, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/payments/{id}/{action}");
        if (amount is not null) request.Content = JsonContent.Create(new { amountMinor = amount.Value });
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }
}
