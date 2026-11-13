using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AccountOpenedMessage = Ledgerline.Contracts.Accounts.AccountOpened;

namespace Ledgerline.Accounts.IntegrationTests;

public sealed class AccountsApiTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Open_account_returns_201_with_zero_balance_and_publishes_AccountOpened()
    {
        await using var host = await StartAsync();
        var owner = Ids.New();
        using var client = host.CreateClient(owner, email: "alice@example.test");
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.account-opened.v1");
        using var response = await OpenAsync(client);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var account = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = account.GetProperty("id").GetGuid();
        response.Headers.Location!.OriginalString.ShouldBe("/accounts/" + id);
        account.GetProperty("ownerId").GetGuid().ShouldBe(owner);
        account.GetProperty("balanceMinor").GetInt64().ShouldBe(0);
        account.GetProperty("heldMinor").GetInt64().ShouldBe(0);
        account.GetProperty("version").GetInt32().ShouldBe(1);
        var opened = await probe.WaitForAsync<AccountOpenedMessage>(x => x.AccountId == id);
        opened.OwnerId.ShouldBe(owner);
        opened.ContactEmail.ShouldBe("alice@example.test");
    }

    [Fact]
    public async Task Open_account_without_idempotency_key_returns_400()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient(Ids.New());
        using var response = await OpenAsync(client, includeKey: false);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Customer_cannot_open_merchant_wallet()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient(Ids.New());
        using var response = await OpenAsync(client, type: "MerchantWallet");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Second_account_of_same_type_and_currency_returns_409()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient(Ids.New());
        using var first = await OpenAsync(client);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var second = await OpenAsync(client);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Get_foreign_account_returns_404()
    {
        await using var host = await StartAsync();
        using var owner = host.CreateClient(Ids.New());
        using var opened = await OpenAsync(owner);
        var account = await opened.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        using var stranger = host.CreateClient(Ids.New());
        using var response = await stranger.GetAsync("/accounts/" + account.GetProperty("id").GetGuid(), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_entries_is_newest_first_and_paginates()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient(Ids.New());
        using var opened = await OpenAsync(client);
        var account = await opened.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = account.GetProperty("id").GetGuid();
        for (var i = 1; i <= 5; i++) await TopUpAsync(host, id, i * 100);
        var amounts = new List<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/accounts/{id}/entries?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await client.GetFromJsonAsync<JsonElement>(url, TestContext.Current.CancellationToken);
            amounts.AddRange(page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("amountMinor").GetInt64()));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null);
        pages.ShouldBe(3);
        amounts.ShouldBe([500L, 400L, 300L, 200L, 100L]);
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    [Fact]
    public async Task Unsupported_currency_returns_400()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient(Ids.New());
        using var response = await OpenAsync(client, currency: "GBP");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private Task<ServiceHost<Program>> StartAsync() => ServiceHost<Program>.StartAsync(infrastructure, "accounts_api");

    private static async Task<HttpResponseMessage> OpenAsync(HttpClient client, string currency = "TRY", string type = "CustomerWallet",
        string? key = null, bool includeKey = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/accounts") { Content = JsonContent.Create(new { type, currency }) };
        if (includeKey) request.Headers.Add("Idempotency-Key", key ?? Ids.New().ToString("N"));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task TopUpAsync(ServiceHost<Program> host, Guid account, long amount)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        (await scope.ServiceProvider.GetRequiredService<ILedger>().PostAsync(Entries.TopUp(Ids.New(), Ids.New(), account,
            Money.Create(amount, "TRY")), TestContext.Current.CancellationToken)).ShouldBeOfType<PostingResult.Posted>();
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }
}
