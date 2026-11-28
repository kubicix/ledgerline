using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Reporting.Infrastructure;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using AccountsProgram = Ledgerline.Accounts.Program;

namespace Ledgerline.Reporting.IntegrationTests;

/// <summary>
/// The read model's two consistency proofs (reporting.md section 6): a rebuild from offset zero gives the same
/// documents (Q2), and after catch-up every account's newest statement balance is the ledger balance (Q3).
/// </summary>
public sealed class RebuildTests(InfrastructureFixture infrastructure)
{
    private const string AccountsLedgerTopic = "ledgerline.ledger.entries.v1";
    private const string AccountsEventsTopic = "ledgerline.accounts.events.v1";
    private const int Seed = 20261128;
    private static readonly string[] Collections =
    [
        MongoCollections.AccountStatementsName, MongoCollections.PaymentTimelinesName,
        MongoCollections.MerchantDailyTotalsName, MongoCollections.AccountOwnersName
    ];
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Rebuild_from_offset_zero_produces_identical_documents()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        await FeedAsync(host);
        await host.WaitForCatchUpAsync(ReportingApiTests.KafkaWait);
        var before = await ExportAsync(host);
        foreach (var name in Collections) before[name].ShouldNotBeEmpty(name);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(host.Settings).Build();

        // While the group has a live member the command refuses, and drops nothing.
        var refused = await Should.ThrowAsync<InvalidOperationException>(() => RebuildCommand.RunAsync(configuration, TestContext.Current.CancellationToken));
        refused.Message.ShouldContain(host.Options.GroupId);
        foreach (var name in Collections)
            (await CountAsync(host, name)).ShouldBe(before[name].Count, name);

        await host.StopAsync();
        await RebuildCommand.RunAsync(configuration, TestContext.Current.CancellationToken);
        foreach (var name in Collections) (await CountAsync(host, name)).ShouldBe(0, name);
        foreach (var topic in host.Options.EventTopics)
            for (var partition = 0; partition < ReportingHost.EventPartitions; partition++)
                (await host.CommittedOffsetAsync(new TopicPartition(topic, partition))).ShouldBe((long?)0, $"{topic} [{partition}]");
        await AssertIndexAsync(host, MongoCollections.AccountStatementsName, new BsonDocument { { "accountId", 1 }, { "postedAt", -1 } });

        host.StartAgain();
        await host.WaitForCatchUpAsync(ReportingApiTests.KafkaWait);
        var after = await ExportAsync(host);
        foreach (var name in Collections)
        {
            after[name].Count.ShouldBe(before[name].Count, name);
            for (var i = 0; i < before[name].Count; i++)
            {
                var (expected, actual) = (WithoutUpdatedAt(before[name][i]), WithoutUpdatedAt(after[name][i]));
                actual.ShouldBe(expected, $"{name} differs after the rebuild.\nbefore: {expected.ToJson()}\nafter:  {actual.ToJson()}");
            }
        }
    }

    [Fact]
    public async Task Latest_statement_balance_equals_ledger_balance_after_catch_up()
    {
        await using var reporting = await ReportingHost.StartAsync(infrastructure, ledgerEntriesTopic: AccountsLedgerTopic, accountEventsTopic: AccountsEventsTopic);
        await using var accounts = await ServiceHost<AccountsProgram>.StartAsync(infrastructure, "reporting_q3", settings: new Dictionary<string, string?>
        {
            ["Messaging:Kafka:Enabled"] = "true",
            ["Messaging:Kafka:BootstrapServers"] = infrastructure.KafkaBootstrapServers
        });
        var owners = Enumerable.Range(0, 4).Select(_ => Ids.New()).ToArray();
        var wallets = new List<Guid>();
        foreach (var owner in owners) wallets.Add(await OpenAccountAsync(accounts, owner));
        await using var probe = await BrokerProbe.CreateAsync(accounts.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.#");

        // 200 postings, one at a time: each waits for the ledger's reply, so an account's postings are strictly ordered.
        var random = new Random(Seed);
        var balances = wallets.ToDictionary(x => x, _ => 0L);
        for (var i = 0; i < 200; i++)
        {
            var operation = Ids.New();
            var funded = wallets.Where(x => balances[x] > 0).ToList();
            if (funded.Count > 0 && random.Next(3) > 0)
            {
                var from = funded[random.Next(funded.Count)];
                var others = wallets.Where(x => x != from).ToList();
                var to = others[random.Next(others.Count)];
                var amount = 1 + random.NextInt64(Math.Min(balances[from], 5_000));
                await probe.PublishAsync(new TransferFunds(operation, Ids.New(), from, to, amount, "TRY"), operation);
                balances[from] -= amount;
                balances[to] += amount;
            }
            else
            {
                var to = wallets[random.Next(wallets.Count)];
                var amount = 1 + random.NextInt64(10_000);
                await probe.PublishAsync(new CreditTopUp(operation, Ids.New(), to, amount, "TRY"), operation);
                balances[to] += amount;
            }
            await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == operation, TimeSpan.FromSeconds(30));
        }

        // Caught up: one statement document per ledger line of these accounts, and their owners known. Neither
        // condition looks at a balance.
        var mine = wallets.Select(x => x.ToString()).ToList();
        var statements = reporting.Database.GetCollection<BsonDocument>(MongoCollections.AccountStatementsName);
        var owned = reporting.Database.GetCollection<BsonDocument>(MongoCollections.AccountOwnersName);
        long ledgerLines;
        await using (var scope = accounts.Services.CreateAsyncScope())
            ledgerLines = await scope.ServiceProvider.GetRequiredService<AccountsDbContext>().JournalLines
                .LongCountAsync(x => wallets.Contains(x.AccountId), TestContext.Current.CancellationToken);
        ledgerLines.ShouldBeGreaterThanOrEqualTo(200);
        await Eventually.ShouldAsync(async () =>
                await statements.CountDocumentsAsync(Builders<BsonDocument>.Filter.In("accountId", mine), cancellationToken: TestContext.Current.CancellationToken) == ledgerLines
                && await owned.CountDocumentsAsync(Builders<BsonDocument>.Filter.In("_id", mine), cancellationToken: TestContext.Current.CancellationToken) == wallets.Count,
            TimeSpan.FromSeconds(120), "Reporting did not catch up with the ledger.");

        for (var i = 0; i < wallets.Count; i++)
        {
            var wallet = wallets[i];
            var newest = await statements.Find(new BsonDocument("accountId", wallet.ToString()))
                .Sort(new BsonDocument { { "postedAt", -1 }, { "_id", -1 } }).Limit(2).ToListAsync(TestContext.Current.CancellationToken);
            newest.Count.ShouldBe(2, wallet.ToString());
            // "Newest" must be decided by postedAt alone; a tie at millisecond precision would make it arbitrary.
            newest[0]["postedAt"].ShouldNotBe(newest[1]["postedAt"], $"Two newest lines of {wallet} share postedAt (seed {Seed}).");
            var ledgerBalance = await BalanceAsync(accounts, owners[i], wallet);
            ledgerBalance.ShouldBe(balances[wallet]);
            newest[0]["balanceAfterMinor"].ShouldBe(new BsonInt64(ledgerBalance), $"Account {wallet} (seed {Seed}).");

            // The API reads the same line, through Reporting's own ownership record.
            using var client = reporting.CreateClient(owners[i]);
            using var response = await client.GetAsync(new Uri($"/accounts/{wallet}/statement?limit=1", UriKind.Relative), TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            page.GetProperty("items")[0].GetProperty("balanceAfterMinor").GetInt64().ShouldBe(ledgerBalance);
        }
        await LedgerAssert.InvariantsHoldAsync(accounts.ConnectionString);
    }

    /// <summary>
    /// Records for all four collections, spread over the partitions: accounts with and without an owner, ledger lines,
    /// several merchant payments captured and refunded on one merchant and day (so their keys land on different
    /// partitions and arrive in a mixed order), an older event after a newer one, and redelivered duplicates.
    /// </summary>
    private static async Task FeedAsync(ReportingHost host)
    {
        var random = new Random(Seed);
        var accounts = Enumerable.Range(0, 6).Select(_ => Ids.New()).ToList();
        foreach (var account in accounts) await TestRecords.OpenAsync(host, account, Ids.New());
        await TestRecords.OpenAsync(host, Ids.New(), null);
        var balances = accounts.ToDictionary(x => x, _ => 0L);
        for (var i = 0; i < 30; i++)
        {
            var account = accounts[random.Next(accounts.Count)];
            var amount = 1 + random.NextInt64(1_000);
            balances[account] += amount;
            await TestRecords.PostAsync(host, TestRecords.Line(account, T0.AddSeconds(i), balances[account], amount: amount));
        }

        var merchant = accounts[0];
        var duplicates = new List<(Guid Payment, TestRecords.Record Record)>();
        for (var i = 0; i < 8; i++)
        {
            var payment = Ids.New();
            var customer = Ids.New();
            var at = T0.AddMinutes(i);
            var authorized = TestRecords.Wrap(new PaymentAuthorized(payment, "MerchantPayment", customer, 10_000, "TRY", accounts[1], merchant, at, at.AddDays(7)));
            var captured = TestRecords.Wrap(new PaymentCaptured(payment, "MerchantPayment", customer, 10_000, "TRY", accounts[1], merchant, at.AddSeconds(1), 9_000 + i));
            var completed = TestRecords.Wrap(new PaymentCompleted(payment, "MerchantPayment", customer, 10_000, "TRY", accounts[1], merchant, at.AddSeconds(2), null));
            // Every other payment: the newer capture arrives before the authorization.
            var sequence = i % 2 == 0 ? new[] { authorized, captured, completed } : [captured, authorized, completed];
            foreach (var record in sequence) await TestRecords.PaymentAsync(host, payment, record);
            if (i % 3 == 0)
            {
                var refunded = TestRecords.Wrap(new PaymentRefunded(payment, "MerchantPayment", customer, 10_000, "TRY", accounts[1], merchant,
                    at.AddSeconds(3), Ids.New(), 100 + i, 100 + i));
                await TestRecords.PaymentAsync(host, payment, refunded);
                duplicates.Add((payment, refunded));
            }
            duplicates.Add((payment, captured));
        }
        var topUp = Ids.New();
        await TestRecords.PaymentAsync(host, topUp, TestRecords.Wrap(new PaymentAuthorized(topUp, "TopUp", Ids.New(), 500, "USD", null, accounts[2], T0, T0.AddDays(7))));
        // Redelivery: the same records again, on the same keys.
        foreach (var (payment, record) in duplicates) await TestRecords.PaymentAsync(host, payment, record);
    }

    private static async Task<Dictionary<string, List<BsonDocument>>> ExportAsync(ReportingHost host)
    {
        var export = new Dictionary<string, List<BsonDocument>>();
        foreach (var name in Collections)
            export[name] = await host.Database.GetCollection<BsonDocument>(name).Find(new BsonDocument())
                .Sort(new BsonDocument("_id", 1)).ToListAsync(TestContext.Current.CancellationToken);
        return export;
    }

    private static BsonDocument WithoutUpdatedAt(BsonDocument document)
    {
        var copy = document.DeepClone().AsBsonDocument;
        copy.Remove("updatedAt");
        return copy;
    }

    private static Task<long> CountAsync(ReportingHost host, string name) =>
        host.Database.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument(), cancellationToken: TestContext.Current.CancellationToken);

    private static async Task AssertIndexAsync(ReportingHost host, string collection, BsonDocument key)
    {
        using var cursor = await host.Database.GetCollection<BsonDocument>(collection).Indexes.ListAsync(TestContext.Current.CancellationToken);
        (await cursor.ToListAsync(TestContext.Current.CancellationToken)).ShouldContain(index => index["key"].AsBsonDocument == key);
    }

    private static async Task<Guid> OpenAccountAsync(ServiceHost<AccountsProgram> host, Guid owner)
    {
        using var client = host.CreateClient(owner);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/accounts") { Content = JsonContent.Create(new { type = "CustomerWallet", currency = "TRY" }) };
        request.Headers.Add("Idempotency-Key", Ids.New().ToString("N"));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
    }

    /// <summary>The ledger balance as Accounts serves it to the owner.</summary>
    private static async Task<long> BalanceAsync(ServiceHost<AccountsProgram> host, Guid owner, Guid account)
    {
        using var client = host.CreateClient(owner);
        using var response = await client.GetAsync(new Uri($"/accounts/{account}", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("balanceMinor").GetInt64();
    }
}
