using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Reporting.Infrastructure;
using Ledgerline.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Ledgerline.Reporting.IntegrationTests;

/// <summary>
/// The read-only HTTP API (reporting.md sections 5 and 7). Ownership comes from Reporting's own
/// <c>account_owners</c>, fed here by <c>account-opened</c> records; an unknown account, a non-owner and a non-party all
/// get 404, as on Accounts and Payments.
/// </summary>
public sealed class ReportingApiTests(InfrastructureFixture infrastructure)
{
    internal static readonly TimeSpan KafkaWait = TimeSpan.FromSeconds(60);
    private const string LagHeader = "X-Read-Model-Lag-Seconds";
    private const string ValidationFailed = "urn:ledgerline:problem:validation-failed";
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Statement_is_paginated_newest_first_and_owner_only()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        var owner = Ids.New();
        var account = Ids.New();
        var otherAccount = Ids.New();
        await TestRecords.OpenAsync(host, account, owner);
        await TestRecords.OpenAsync(host, otherAccount, Ids.New());
        // Two lines share 10:04 and fall on both sides of the first page boundary (limit 3).
        int[] minutes = [1, 2, 3, 4, 4, 5, 6];
        var lines = minutes.Select((minute, i) => TestRecords.Line(account, T0.AddMinutes(minute), balanceAfter: 1000 + i)).ToList();
        foreach (var line in lines) await TestRecords.PostAsync(host, line);
        await TestRecords.PostAsync(host, TestRecords.Line(otherAccount, T0.AddMinutes(7), balanceAfter: 99));
        await host.WaitForCatchUpAsync(KafkaWait);

        var expected = lines.OrderByDescending(x => x.PostedAt).ThenByDescending(x => $"{x.JournalEntryId}:{x.LineNo}", StringComparer.Ordinal)
            .Select(x => x.JournalEntryId).ToList();
        using var client = host.CreateClient(owner);
        var pages = new List<List<JsonElement>>();
        string? cursor = null;
        do
        {
            var page = await GetJsonAsync(client, $"/accounts/{account}/statement?limit=3" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            pages.Add([.. page.GetProperty("items").EnumerateArray()]);
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
        } while (cursor is not null && pages.Count < 10);
        pages.Select(x => x.Count).ShouldBe([3, 3, 1]);
        pages.SelectMany(x => x).Select(x => x.GetProperty("journalEntryId").GetGuid()).ShouldBe(expected);

        // Items are shaped like Accounts' entries.
        var newest = pages[0][0];
        newest.EnumerateObject().Select(x => x.Name).ShouldBe(["journalEntryId", "entryType", "direction", "amountMinor", "currency",
            "balanceAfterMinor", "paymentId", "postedAt"]);
        var top = lines[^1];
        newest.GetProperty("entryType").GetString().ShouldBe("Transfer");
        newest.GetProperty("direction").GetString().ShouldBe("Credit");
        newest.GetProperty("amountMinor").GetInt64().ShouldBe(top.AmountMinor);
        newest.GetProperty("currency").GetString().ShouldBe("TRY");
        newest.GetProperty("balanceAfterMinor").GetInt64().ShouldBe(top.BalanceAfterMinor);
        newest.GetProperty("paymentId").GetGuid().ShouldBe(top.PaymentId);
        newest.GetProperty("postedAt").GetDateTimeOffset().ShouldBe(top.PostedAt);

        // from and to are inclusive bounds on postedAt.
        var window = await GetJsonAsync(client, $"/accounts/{account}/statement?from={Query(T0.AddMinutes(2))}&to={Query(T0.AddMinutes(4))}");
        window.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("journalEntryId").GetGuid())
            .ShouldBe(expected.Skip(2).Take(4));
        window.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        // Admin sees any account; anyone else, and any unknown account, gets 404.
        using var admin = host.CreateClient(Ids.New(), "admin");
        (await GetJsonAsync(admin, $"/accounts/{account}/statement")).GetProperty("items").GetArrayLength().ShouldBe(7);
        using var stranger = host.CreateClient(Ids.New());
        await ShouldBeAsync(stranger, $"/accounts/{account}/statement", HttpStatusCode.NotFound);
        await ShouldBeAsync(client, $"/accounts/{otherAccount}/statement", HttpStatusCode.NotFound);
        await ShouldBeAsync(client, $"/accounts/{Ids.New()}/statement", HttpStatusCode.NotFound);
        await ShouldBeAsync(admin, $"/accounts/{Ids.New()}/statement", HttpStatusCode.NotFound);
        using var anonymous = host.CreateClient();
        await ShouldBeAsync(anonymous, $"/accounts/{account}/statement", HttpStatusCode.Unauthorized);

        foreach (var query in new[] { "limit=0", "limit=101", "cursor=not-a-cursor", $"from={Query(T0.AddMinutes(5))}&to={Query(T0.AddMinutes(1))}" })
        {
            var problem = await ShouldBeAsync(client, $"/accounts/{account}/statement?{query}", HttpStatusCode.BadRequest);
            problem.GetProperty("type").GetString().ShouldBe(ValidationFailed, query);
        }
    }

    [Fact]
    public async Task Timeline_is_visible_to_both_parties_and_admin()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        var customer = Ids.New();
        var recipient = Ids.New();
        var source = Ids.New();
        var destination = Ids.New();
        await TestRecords.OpenAsync(host, source, customer);
        await TestRecords.OpenAsync(host, destination, recipient);
        var payment = Ids.New();
        var authorized = TestRecords.Wrap(new PaymentAuthorized(payment, "Transfer", customer, 2500, "TRY", source, destination, T0, T0.AddDays(7)));
        var captured = TestRecords.Wrap(new PaymentCaptured(payment, "Transfer", customer, 2500, "TRY", source, destination, T0.AddSeconds(1), 2500));
        await TestRecords.PaymentAsync(host, payment, authorized);
        await TestRecords.PaymentAsync(host, payment, captured);
        // A payment whose customer is someone else, from an account the customer owns: the source owner is a party too.
        var fromOwnedSource = Ids.New();
        await TestRecords.PaymentAsync(host, fromOwnedSource, TestRecords.Wrap(new PaymentAuthorized(fromOwnedSource, "Transfer", Ids.New(), 100,
            "TRY", source, Ids.New(), T0, T0.AddDays(7))));
        await host.WaitForCatchUpAsync(KafkaWait);

        using var customerClient = host.CreateClient(customer);
        var timeline = await GetJsonAsync(customerClient, $"/payments/{payment}/timeline");
        timeline.EnumerateObject().Select(x => x.Name).ShouldBe(["paymentId", "kind", "status", "amountMinor", "currency", "customerId",
            "sourceAccountId", "destinationAccountId", "capturedMinor", "refundedMinor", "events", "updatedAt"]);
        timeline.GetProperty("paymentId").GetGuid().ShouldBe(payment);
        timeline.GetProperty("kind").GetString().ShouldBe("Transfer");
        timeline.GetProperty("status").GetString().ShouldBe("Captured");
        timeline.GetProperty("amountMinor").GetInt64().ShouldBe(2500);
        timeline.GetProperty("currency").GetString().ShouldBe("TRY");
        timeline.GetProperty("customerId").GetGuid().ShouldBe(customer);
        timeline.GetProperty("sourceAccountId").GetGuid().ShouldBe(source);
        timeline.GetProperty("destinationAccountId").GetGuid().ShouldBe(destination);
        timeline.GetProperty("capturedMinor").GetInt64().ShouldBe(2500);
        timeline.GetProperty("refundedMinor").GetInt64().ShouldBe(0);
        timeline.GetProperty("updatedAt").GetDateTimeOffset().ShouldBe(T0.AddSeconds(1));
        var events = timeline.GetProperty("events").EnumerateArray().ToList();
        events.Select(x => x.GetProperty("messageId").GetGuid()).ShouldBe([authorized.MessageId, captured.MessageId]);
        events.Select(x => x.GetProperty("type").GetString()).ShouldBe(["payment-authorized", "payment-captured"]);
        events.Select(x => x.GetProperty("at").GetDateTimeOffset()).ShouldBe([T0, T0.AddSeconds(1)]);

        using var recipientClient = host.CreateClient(recipient);
        (await GetJsonAsync(recipientClient, $"/payments/{payment}/timeline")).GetProperty("paymentId").GetGuid().ShouldBe(payment);
        using var admin = host.CreateClient(Ids.New(), "admin");
        (await GetJsonAsync(admin, $"/payments/{payment}/timeline")).GetProperty("paymentId").GetGuid().ShouldBe(payment);
        (await GetJsonAsync(customerClient, $"/payments/{fromOwnedSource}/timeline")).GetProperty("paymentId").GetGuid().ShouldBe(fromOwnedSource);
        await ShouldBeAsync(recipientClient, $"/payments/{fromOwnedSource}/timeline", HttpStatusCode.NotFound);

        using var stranger = host.CreateClient(Ids.New());
        await ShouldBeAsync(stranger, $"/payments/{payment}/timeline", HttpStatusCode.NotFound);
        await ShouldBeAsync(admin, $"/payments/{Ids.New()}/timeline", HttpStatusCode.NotFound);
        using var anonymous = host.CreateClient();
        await ShouldBeAsync(anonymous, $"/payments/{payment}/timeline", HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Daily_totals_are_listed_by_day_for_the_merchant_owner_and_admin()
    {
        await using var host = await ReportingHost.StartAsync(infrastructure);
        var merchantOwner = Ids.New();
        var merchant = Ids.New();
        await TestRecords.OpenAsync(host, merchant, merchantOwner);
        foreach (var (day, currency, amount) in new[] { (2, "TRY", 300L), (0, "TRY", 100L), (1, "USD", 50L), (1, "TRY", 200L), (3, "TRY", 400L) })
        {
            var payment = Ids.New();
            await TestRecords.PaymentAsync(host, payment, TestRecords.Wrap(new PaymentCaptured(payment, "MerchantPayment", Ids.New(), amount, currency,
                Ids.New(), merchant, T0.AddDays(day), amount)));
        }
        var refunded = Ids.New();
        await TestRecords.PaymentAsync(host, refunded, TestRecords.Wrap(new PaymentRefunded(refunded, "MerchantPayment", Ids.New(), 100, "TRY",
            Ids.New(), merchant, T0.AddDays(1).AddHours(1), Ids.New(), 40, 40)));
        await host.WaitForCatchUpAsync(KafkaWait);

        using var owner = host.CreateClient(merchantOwner, "merchant");
        var totals = await GetJsonAsync(owner, $"/merchants/{merchant}/daily-totals?from=2026-11-14&to=2026-11-15");
        var items = totals.EnumerateArray().ToList();
        items.Select(x => (x.GetProperty("day").GetString(), x.GetProperty("currency").GetString()))
            .ShouldBe([("2026-11-14", "TRY"), ("2026-11-14", "USD"), ("2026-11-15", "TRY")]);
        items[0].EnumerateObject().Select(x => x.Name).ShouldBe(["merchantAccountId", "day", "currency", "capturedMinor", "refundedMinor",
            "captureCount", "refundCount"]);
        items[0].GetProperty("merchantAccountId").GetGuid().ShouldBe(merchant);
        items[0].GetProperty("capturedMinor").GetInt64().ShouldBe(200);
        items[0].GetProperty("refundedMinor").GetInt64().ShouldBe(40);
        items[0].GetProperty("captureCount").GetInt64().ShouldBe(1);
        items[0].GetProperty("refundCount").GetInt64().ShouldBe(1);
        (await GetJsonAsync(owner, $"/merchants/{merchant}/daily-totals")).EnumerateArray()
            .Select(x => x.GetProperty("day").GetString()).ShouldBe(["2026-11-13", "2026-11-14", "2026-11-14", "2026-11-15", "2026-11-16"]);

        using var admin = host.CreateClient(Ids.New(), "admin");
        (await GetJsonAsync(admin, $"/merchants/{merchant}/daily-totals")).GetArrayLength().ShouldBe(5);
        using var stranger = host.CreateClient(Ids.New(), "merchant");
        await ShouldBeAsync(stranger, $"/merchants/{merchant}/daily-totals", HttpStatusCode.NotFound);
        await ShouldBeAsync(admin, $"/merchants/{Ids.New()}/daily-totals", HttpStatusCode.NotFound);
        var problem = await ShouldBeAsync(owner, $"/merchants/{merchant}/daily-totals?from=2026-11-15&to=2026-11-14", HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().ShouldBe(ValidationFailed);
    }

    [Fact]
    public async Task Lag_header_is_present()
    {
        var faults = new MongoFaults();
        await using var host = await ReportingHost.StartAsync(infrastructure, services =>
        {
            services.RemoveAll<MongoCollections>();
            services.AddSingleton<MongoCollections>(provider => new FaultyMongoCollections(
                provider.GetRequiredService<IMongoDatabase>(), faults, MongoCollections.AccountStatementsName));
        });
        var owner = Ids.New();
        var account = Ids.New();
        await TestRecords.OpenAsync(host, account, owner);
        await host.WaitForCatchUpAsync(KafkaWait);
        using var metrics = MetricRecorder.ForMeter("Ledgerline", host.Services.GetRequiredService<IMeterFactory>());

        // Caught up: zero, on success, on a missing resource and on an unauthenticated call alike.
        using var client = host.CreateClient(owner);
        using var anonymous = host.CreateClient();
        foreach (var (http, path, status) in new[]
                 {
                     (client, $"/accounts/{account}/statement", HttpStatusCode.OK),
                     (client, $"/payments/{Ids.New()}/timeline", HttpStatusCode.NotFound),
                     (anonymous, $"/accounts/{account}/statement", HttpStatusCode.Unauthorized)
                 })
        {
            using var response = await http.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(status);
            LagOf(response).ShouldBe(0, path);
        }
        metrics.RecordObservableInstruments();
        var reading = metrics.Of("ledgerline.reporting.lag").ShouldHaveSingleItem();
        reading.Unit.ShouldBe("s");
        Convert.ToDouble(reading.Value, CultureInfo.InvariantCulture).ShouldBe(0);

        // A record created an hour ago that the loop cannot apply while MongoDB fails: the read model misses a
        // change that is an hour old, so the lag is at least an hour.
        faults.Failing = true;
        await host.ProduceAsync(host.Options.LedgerEntriesTopic, account.ToString(),
            TestRecords.Serialize(TestRecords.Line(account, T0, balanceAfter: 1)), DateTimeOffset.UtcNow.AddHours(-1));
        await Eventually.ShouldAsync(() => Task.FromResult(faults.Failures > 0), KafkaWait, "The loop did not take the record.");
        using (var behind = await anonymous.GetAsync(new Uri($"/accounts/{account}/statement", UriKind.Relative), TestContext.Current.CancellationToken))
        {
            behind.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            LagOf(behind).ShouldBeInRange(3600, 3700);
        }
        metrics.RecordObservableInstruments();
        Convert.ToDouble(metrics.Of("ledgerline.reporting.lag")[^1].Value, CultureInfo.InvariantCulture).ShouldBeInRange(3600, 3700);

        faults.Failing = false;
        await host.WaitForCatchUpAsync(KafkaWait);
        using var caughtUp = await client.GetAsync(new Uri($"/accounts/{account}/statement", UriKind.Relative), TestContext.Current.CancellationToken);
        caughtUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        LagOf(caughtUp).ShouldBe(0);
    }

    private static long LagOf(HttpResponseMessage response)
    {
        response.Headers.TryGetValues(LagHeader, out var values).ShouldBeTrue($"{LagHeader} is missing.");
        var text = values!.ShouldHaveSingleItem();
        text.ShouldMatch("^[0-9]+$");
        return long.Parse(text, CultureInfo.InvariantCulture);
    }

    private static string Query(DateTimeOffset at) => Uri.EscapeDataString(at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ShouldBeAsync(HttpClient client, string path, HttpStatusCode status)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(status, path);
        if (status == HttpStatusCode.Unauthorized) return default;
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }
}

/// <summary>Builds and produces the records the Reporting tests feed in, in the shapes the write services relay.</summary>
internal static class TestRecords
{
    public static JournalEntryLinePosted Line(Guid account, DateTimeOffset postedAt, long balanceAfter, string direction = "Credit", long amount = 250) =>
        new(Ids.New(), "Transfer", Ids.New(), "TRY", postedAt, account, direction, amount, balanceAfter, 1);

    public static byte[] Serialize(JournalEntryLinePosted line) => Wrap(line, line.PostedAt).Value;

    public static Task PostAsync(ReportingHost host, JournalEntryLinePosted line) =>
        host.ProduceAsync(host.Options.LedgerEntriesTopic, line.AccountId.ToString(), Serialize(line));

    public static Task OpenAsync(ReportingHost host, Guid account, Guid? owner) =>
        host.ProduceAsync(host.Options.AccountEventsTopic, account.ToString(),
            Wrap(new AccountOpened(account, owner, owner is null ? "System" : "CustomerWallet", "TRY", null, T0)).Value);

    public static Task PaymentAsync(ReportingHost host, Guid payment, Record record) =>
        host.ProduceAsync(host.Options.PaymentEventsTopic, payment.ToString(), record.Value);

    public static Record Wrap<T>(T payload, DateTimeOffset? occurredAt = null) where T : notnull
    {
        var type = typeof(T).GetCustomAttribute<MessageTypeAttribute>()!.Name;
        var envelope = new MessageEnvelope(Ids.New(), type, occurredAt ?? T0, Ids.New(), null, null, EnvelopeSerializer.ToPayload(payload));
        return new Record(envelope.MessageId, EnvelopeSerializer.Serialize(envelope));
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    public sealed record Record(Guid MessageId, byte[] Value);
}
