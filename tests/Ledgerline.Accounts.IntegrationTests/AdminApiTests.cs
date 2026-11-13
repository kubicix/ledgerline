using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StatusMessage = Ledgerline.Contracts.Accounts.AccountStatusChanged;

namespace Ledgerline.Accounts.IntegrationTests;

public sealed class AdminApiTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Freeze_requires_admin()
    {
        await using var host = await StartAsync();
        var wallet = await WalletAsync(host);
        using var customer = host.CreateClient(Guid.NewGuid());
        using var denied = await PostAsync(customer, wallet, "freeze");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Freeze_appends_status_event_and_publishes_AccountStatusChanged()
    {
        await using var host = await StartAsync();
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.#");
        var wallet = await WalletAsync(host);
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");
        using var response = await PostAsync(admin, wallet, "freeze");
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await probe.WaitForAsync<StatusMessage>(x => x.AccountId == wallet)).Status.ShouldBe("Frozen");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        var stored = await db.Events.SingleAsync(x => x.StreamId == wallet && x.Type == "account-status-changed.v1", TestContext.Current.CancellationToken);
        stored.Version.ShouldBe(2);
        stored.Data.GetProperty("status").GetString().ShouldBe("Frozen");
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    [Fact]
    public async Task Freeze_twice_is_a_no_op_204()
    {
        await using var host = await StartAsync();
        var wallet = await WalletAsync(host);
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");
        using var first = await PostAsync(admin, wallet, "freeze");
        using var second = await PostAsync(admin, wallet, "freeze");
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        (await db.Events.CountAsync(x => x.Type == "account-status-changed.v1", TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.Set<OutboxMessage>().CountAsync(x => x.Type == "accounts.event.account-status-changed.v1", TestContext.Current.CancellationToken)).ShouldBe(1);
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    [Fact]
    public async Task System_account_cannot_be_frozen_409()
    {
        await using var host = await StartAsync();
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");
        using var response = await PostAsync(admin, SystemAccounts.IdFor(SystemAccounts.BankClearing, "TRY"), "freeze");
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Unfreeze_allows_postings_again()
    {
        await using var host = await StartAsync();
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.#");
        var wallet = await WalletAsync(host);
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");
        using var frozen = await PostAsync(admin, wallet, "freeze");
        frozen.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var blocked = Guid.NewGuid();
        await probe.PublishAsync(new CreditTopUp(blocked, Guid.NewGuid(), wallet, 100, "TRY"), blocked);
        (await probe.WaitForAsync<LedgerOperationRejected>(x => x.OperationId == blocked)).ReasonCode.ShouldBe("AccountFrozen");
        using var unfrozen = await PostAsync(admin, wallet, "unfreeze");
        unfrozen.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var allowed = Guid.NewGuid();
        await probe.PublishAsync(new CreditTopUp(allowed, Guid.NewGuid(), wallet, 100, "TRY"), allowed);
        await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == allowed);
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    [Fact]
    public async Task Trial_balance_is_zero_after_mixed_postings()
    {
        await using var host = await StartAsync();
        var from = await WalletAsync(host);
        var to = await WalletAsync(host);
        await PostEntryAsync(host, Entries.TopUp(Guid.NewGuid(), Guid.NewGuid(), from, Money.Create(1000, "TRY")));
        await PostEntryAsync(host, Entries.Transfer(Guid.NewGuid(), Guid.NewGuid(), from, to, Money.Create(250, "TRY")));
        await PostEntryAsync(host, Entries.Refund(Guid.NewGuid(), Guid.NewGuid(), to, from, Money.Create(100, "TRY")));
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");
        var report = await admin.GetFromJsonAsync<JsonElement>("/ledger/trial-balance", TestContext.Current.CancellationToken);
        report.GetProperty("asOf").GetDateTimeOffset().ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
        var currencies = report.GetProperty("currencies").EnumerateArray().ToArray();
        currencies.Length.ShouldBe(3);
        currencies.ShouldAllBe(x => x.GetProperty("sumOfBalancesMinor").GetInt64() == 0);
        currencies.Single(x => x.GetProperty("currency").GetString() == "TRY").GetProperty("journalEntries").GetInt32().ShouldBe(3);
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    [Fact]
    public async Task Trial_balance_requires_admin()
    {
        await using var host = await StartAsync();
        using var customer = host.CreateClient(Guid.NewGuid());
        using var response = await customer.GetAsync("/ledger/trial-balance", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private Task<ServiceHost<Program>> StartAsync() => ServiceHost<Program>.StartAsync(infrastructure, "admin_api");
    private static async Task<Guid> WalletAsync(ServiceHost<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await TestAccounts.OpenWalletAsync(scope.ServiceProvider.GetRequiredService<AccountsDbContext>(), Guid.NewGuid(), "TRY");
    }
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string action)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/accounts/{id}/{action}");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
    private static async Task PostEntryAsync(ServiceHost<Program> host, PostingRequest request)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        (await scope.ServiceProvider.GetRequiredService<ILedger>().PostAsync(request, TestContext.Current.CancellationToken)).ShouldBeOfType<PostingResult.Posted>();
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }
}
