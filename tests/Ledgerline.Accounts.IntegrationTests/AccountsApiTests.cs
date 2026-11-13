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

    [Fact]
    public async Task Same_key_same_body_returns_same_account_with_201()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient(Ids.New());
        using var first = await OpenAsync(client, key: "same-key");
        using var second = await OpenAsync(client, key: "same-key");
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        var original = await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var replay = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        replay.GetProperty("id").GetGuid().ShouldBe(original.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Same_key_different_body_returns_422()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient(Ids.New());
        using var first = await OpenAsync(client, key: "reused-key");
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var second = await OpenAsync(client, currency: "USD", key: "reused-key");
        second.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var error = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        error.GetProperty("type").GetString().ShouldBe("urn:ledgerline:problem:idempotency-key-reused");
    }

    public static TheoryData<string> MalformedKeys => new() { new string('a', 65), "has space" };

    [Theory]
    [MemberData(nameof(MalformedKeys))]
    public async Task Malformed_key_returns_400(string key)
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient(Ids.New());
        using var response = await OpenAsync(client, key: key);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ten_concurrent_identical_requests_create_one_account()
    {
        await using var host = await StartAsync();
        var owner = Ids.New();
        using var client = host.CreateClient(owner);
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => OpenAsync(client, key: "concurrent-key")));
        var ids = new List<Guid>();
        foreach (var response in responses)
        {
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
                    using var replay = await OpenAsync(client, key: "concurrent-key");
                    replay.StatusCode.ShouldBe(HttpStatusCode.Created);
                    var replayed = await replay.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
                    ids.Add(replayed.GetProperty("id").GetGuid());
                    continue;
                }
                response.StatusCode.ShouldBe(HttpStatusCode.Created);
                var account = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
                ids.Add(account.GetProperty("id").GetGuid());
            }
        }
        ids.Distinct().Count().ShouldBe(1);
        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AccountsDbContext>().Accounts.CountAsync(a => a.OwnerId == owner, TestContext.Current.CancellationToken)).ShouldBe(1);
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
