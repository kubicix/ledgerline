using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Ledgerline.Reconciliation.Matching;
using Ledgerline.Reconciliation.Runs;
using Ledgerline.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ledgerline.Reconciliation.IntegrationTests;

/// <summary>
/// The admin API of reconciliation.md section 5. The read endpoints run on a host without a scheduler over rows seeded by
/// SQL, so each test shapes the runs it needs. <c>POST /runs</c> is shown end to end once, through the real Quartz
/// trigger and job, and its validation with a recording trigger.
/// </summary>
public sealed class ReconciliationApiTests(InfrastructureFixture infrastructure)
{
    private const string ValidationFailed = "urn:ledgerline:problem:validation-failed";
    private const string NotFound = "urn:ledgerline:problem:not-found";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Start_run_requires_admin_and_returns_202()
    {
        // No test of RunTests uses this date, and the Redis lock of a run is keyed by it.
        var date = new DateOnly(2026, 10, 20);
        // The real scheduler, trigger and job, against a bank that listens nowhere: the run exists at once and then fails.
        await using var host = await StagingHost.StartAsync(infrastructure, extraSettings: new Dictionary<string, string?>
        {
            ["Reconciliation:Scheduler:Enabled"] = "true",
            ["Reconciliation:Run:InputWaitTimeout"] = "00:02:00",
            ["Reconciliation:Run:InputPollInterval"] = "00:00:00.100",
            ["Reconciliation:Lock:Ttl"] = "00:00:30",
            ["Reconciliation:Lock:RenewInterval"] = "00:00:05",
            ["Bank:BaseUrl"] = $"http://127.0.0.1:{ClosedPort()}/",
            ["Bank:ApiKey"] = "unused",
            ["Bank:TimeoutSeconds"] = "2"
        });
        using var anonymous = host.CreateClient();
        using var customer = host.CreateClient(Guid.NewGuid(), "customer");
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");

        using (var response = await anonymous.PostAsJsonAsync("/runs", Body(date), Ct)) response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (var response = await customer.PostAsJsonAsync("/runs", Body(date), Ct)) response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RowCountAsync(host, "SELECT count(*) FROM reconciliation_runs")).ShouldBe(0);

        using var accepted = await admin.PostAsJsonAsync("/runs", Body(date), Ct);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        accepted.Headers.Location!.ToString().ShouldBe("/runs/2026-10-20");
        (await accepted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("businessDate").GetString().ShouldBe("2026-10-20");
        await Eventually.ShouldAsync(async () =>
        {
            using var response = await admin.GetAsync("/runs/2026-10-20", Ct);
            return response.StatusCode == HttpStatusCode.OK
                && (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString() == "Failed";
        }, TimeSpan.FromSeconds(90), "The accepted run did not start and fail against the unreachable bank.");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"businessDate":null}""")]
    [InlineData("""{"businessDate":""}""")]
    [InlineData("""{"businessDate":"13/11/2026"}""")]
    [InlineData("""{"businessDate":"2026-13-45"}""")]
    [InlineData("""{"businessDate":"2026-11-13T00:00:00Z"}""")]
    [InlineData("""{"businessDate":" 2026-11-13"}""")]
    [InlineData("""{"businessDate":"2999-01-01"}""")]
    public async Task Start_run_rejects_a_missing_unparsable_or_future_date(string body)
    {
        var trigger = new RecordingTrigger();
        await using var host = await StartAsync(trigger);
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await admin.PostAsync("/runs", content, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ValidationFailed);
        trigger.Dates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_run_without_a_body_returns_400()
    {
        var trigger = new RecordingTrigger();
        await using var host = await StartAsync(trigger);
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");

        using var response = await admin.PostAsync("/runs", null, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ValidationFailed);
        trigger.Dates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_run_of_a_completed_date_still_returns_202_without_an_idempotency_key()
    {
        var trigger = new RecordingTrigger();
        await using var host = await StartAsync(trigger);
        await SeedRunAsync(host, "2026-11-13", "Completed", matched: 3);
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");

        using var response = await admin.PostAsJsonAsync("/runs", Body(new DateOnly(2026, 11, 13)), Ct);

        // The operation is idempotent by business date: no Idempotency-Key header was sent, and none is needed.
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location!.ToString().ShouldBe("/runs/2026-11-13");
        trigger.Dates.ShouldBe([new DateOnly(2026, 11, 13)]);
    }

    [Fact]
    public async Task Every_endpoint_requires_the_admin_role()
    {
        await using var host = await StartAsync(new RecordingTrigger());
        await SeedRunAsync(host, "2026-11-13", "Completed");
        using var anonymous = host.CreateClient();
        using var customer = host.CreateClient(Guid.NewGuid(), "customer");
        string[] paths = ["/runs", "/runs/2026-11-13", "/runs/2026-11-13/items"];

        foreach (var path in paths)
        {
            using (var response = await anonymous.GetAsync(path, Ct)) response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, path);
            using (var response = await customer.GetAsync(path, Ct)) response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    [Fact]
    public async Task List_runs_is_newest_first_and_pages_with_an_opaque_cursor()
    {
        await using var host = await StartAsync(new RecordingTrigger());
        foreach (var day in new[] { "2026-11-10", "2026-11-12", "2026-11-11", "2026-11-13", "2026-11-09" })
            await SeedRunAsync(host, day, day == "2026-11-13" ? "Failed" : "Completed");
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");

        var first = await admin.GetFromJsonAsync<JsonElement>("/runs?limit=2", Ct);
        Dates(first).ShouldBe(["2026-11-13", "2026-11-12"]);
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await admin.GetFromJsonAsync<JsonElement>("/runs?limit=2&cursor=" + cursor, Ct);
        Dates(second).ShouldBe(["2026-11-11", "2026-11-10"]);
        var third = await admin.GetFromJsonAsync<JsonElement>("/runs?limit=2&cursor=" + second.GetProperty("nextCursor").GetString(), Ct);

        Dates(third).ShouldBe(["2026-11-09"]);
        third.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        var all = await admin.GetFromJsonAsync<JsonElement>("/runs", Ct);
        Dates(all).Count.ShouldBe(5);
        all.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        var newest = all.GetProperty("items")[0];
        newest.GetProperty("status").GetString().ShouldBe("Failed");
        newest.GetProperty("error").GetString().ShouldBe("bank down");
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=-5")]
    [InlineData("limit=abc")]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("cursor=!!!")]
    public async Task List_runs_rejects_an_out_of_range_limit_or_a_bad_cursor(string query)
    {
        await using var host = await StartAsync(new RecordingTrigger());
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");

        using var response = await admin.GetAsync("/runs?" + query, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ValidationFailed);
    }

    [Fact]
    public async Task List_runs_rejects_a_cursor_that_holds_no_date()
    {
        await using var host = await StartAsync(new RecordingTrigger());
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");
        var cursor = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("yesterday"));

        using var response = await admin.GetAsync("/runs?cursor=" + cursor, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ValidationFailed);
    }

    [Fact]
    public async Task Get_run_returns_counts_status_and_timestamps()
    {
        await using var host = await StartAsync(new RecordingTrigger());
        var id = await SeedRunAsync(host, "2026-11-13", "Completed", matched: 7, mismatched: 2, inputsIncomplete: true);
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");

        var run = await admin.GetFromJsonAsync<JsonElement>("/runs/2026-11-13", Ct);

        run.GetProperty("runId").GetGuid().ShouldBe(id);
        run.GetProperty("businessDate").GetString().ShouldBe("2026-11-13");
        run.GetProperty("status").GetString().ShouldBe("Completed");
        (run.GetProperty("matched").GetInt32(), run.GetProperty("mismatched").GetInt32()).ShouldBe((7, 2));
        run.GetProperty("inputsIncomplete").GetBoolean().ShouldBeTrue();
        run.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
        run.GetProperty("startedAt").ValueKind.ShouldBe(JsonValueKind.String);
        run.GetProperty("completedAt").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Get_run_of_an_unknown_date_returns_404()
    {
        await using var host = await StartAsync(new RecordingTrigger());
        await SeedRunAsync(host, "2026-11-12", "Completed");
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");

        // The route exists (a known date answers 200), so the 404 below comes from the lookup, not from a missing route.
        using (var known = await admin.GetAsync("/runs/2026-11-12", Ct)) known.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var response = await admin.GetAsync("/runs/2026-11-13", Ct);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, NotFound);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-13-01")]
    [InlineData("20261113")]
    public async Task Get_run_and_items_of_an_unparsable_date_return_400(string date)
    {
        await using var host = await StartAsync(new RecordingTrigger());
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");

        using (var response = await admin.GetAsync("/runs/" + date, Ct)) await AssertProblemAsync(response, HttpStatusCode.BadRequest, ValidationFailed);
        using (var response = await admin.GetAsync($"/runs/{date}/items", Ct)) await AssertProblemAsync(response, HttpStatusCode.BadRequest, ValidationFailed);
    }

    [Fact]
    public async Task Items_can_be_filtered_by_code()
    {
        await using var host = await StartAsync(new RecordingTrigger());
        var id = await SeedRunAsync(host, "2026-11-13", "Completed", mismatched: 4);
        var other = await SeedRunAsync(host, "2026-11-12", "Completed", mismatched: 1);
        await SeedItemAsync(host, id, "AmountMismatch", "payment-b", 1001, 1000, "TRY");
        await SeedItemAsync(host, id, "BankCaptureNotInLedger", "payment-a", 500, null, "TRY");
        await SeedItemAsync(host, id, "AmountMismatch", "payment-a", 2001, 2000, "EUR");
        await SeedItemAsync(host, id, "LedgerImbalance", "TRY", null, -50, "TRY");
        await SeedItemAsync(host, other, "AmountMismatch", "payment-z", 1, 2, "USD");
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");

        var all = await admin.GetFromJsonAsync<JsonElement>("/runs/2026-11-13/items", Ct);
        var filtered = await admin.GetFromJsonAsync<JsonElement>("/runs/2026-11-13/items?code=AmountMismatch", Ct);

        // A bare array, ordered by code and then reference, of this run's items only.
        all.ValueKind.ShouldBe(JsonValueKind.Array);
        all.EnumerateArray().Select(x => x.GetProperty("code").GetString() + "/" + x.GetProperty("reference").GetString()).ToList()
            .ShouldBe(["AmountMismatch/payment-a", "AmountMismatch/payment-b", "BankCaptureNotInLedger/payment-a", "LedgerImbalance/TRY"]);
        filtered.EnumerateArray().Select(x => x.GetProperty("reference").GetString()).ToList().ShouldBe(["payment-a", "payment-b"]);
        var item = filtered[1];
        (item.GetProperty("bankAmountMinor").GetInt64(), item.GetProperty("ledgerAmountMinor").GetInt64(), item.GetProperty("currency").GetString())
            .ShouldBe((1001L, 1000L, "TRY"));
        var missing = all.EnumerateArray().Single(x => x.GetProperty("code").GetString() == "BankCaptureNotInLedger");
        missing.GetProperty("ledgerAmountMinor").ValueKind.ShouldBe(JsonValueKind.Null);
        var none = await admin.GetFromJsonAsync<JsonElement>("/runs/2026-11-13/items?code=RefundMissingAtBank", Ct);
        none.GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("amountmismatch")]
    [InlineData("AMOUNTMISMATCH")]
    [InlineData("0")]
    [InlineData("Nonsense")]
    [InlineData("")]
    public async Task Items_reject_a_code_that_is_not_a_mismatch_code(string code)
    {
        await using var host = await StartAsync(new RecordingTrigger());
        await SeedRunAsync(host, "2026-11-13", "Completed");
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");

        using var response = await admin.GetAsync("/runs/2026-11-13/items?code=" + code, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ValidationFailed);
    }

    [Fact]
    public async Task Items_of_an_unknown_run_return_404_and_of_a_run_without_items_an_empty_array()
    {
        await using var host = await StartAsync(new RecordingTrigger());
        await SeedRunAsync(host, "2026-11-13", "Running");
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");

        using (var response = await admin.GetAsync("/runs/2026-11-12/items", Ct)) await AssertProblemAsync(response, HttpStatusCode.NotFound, NotFound);
        (await admin.GetFromJsonAsync<JsonElement>("/runs/2026-11-13/items", Ct)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task The_code_filter_accepts_every_mismatch_code()
    {
        await using var host = await StartAsync(new RecordingTrigger());
        await SeedRunAsync(host, "2026-11-13", "Completed");
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");

        foreach (var code in Enum.GetNames<MismatchCode>())
        {
            using var response = await admin.GetAsync("/runs/2026-11-13/items?code=" + code, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, code);
        }
    }

    private Task<StagingHost> StartAsync(RecordingTrigger trigger) =>
        StagingHost.StartAsync(infrastructure, overrides: services => services.AddSingleton<IRunTrigger>(trigger));

    private static object Body(DateOnly date) => new { businessDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };

    private static List<string> Dates(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("businessDate").GetString()!).ToList();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe(type);
    }

    private static async Task<Guid> SeedRunAsync(StagingHost host, string date, string status, int matched = 0, int mismatched = 0,
        bool inputsIncomplete = false)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO reconciliation_runs (id, business_date, status, matched, mismatched, inputs_incomplete, error, started_at, completed_at)
            VALUES (@id, @date, @status, @matched, @mismatched, @incomplete, @error, now() - interval '1 hour', @completed)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("date", DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("matched", matched);
        command.Parameters.AddWithValue("mismatched", mismatched);
        command.Parameters.AddWithValue("incomplete", inputsIncomplete);
        command.Parameters.AddWithValue("error", status == "Failed" ? "bank down" : DBNull.Value);
        command.Parameters.AddWithValue("completed", status == "Completed" ? DateTimeOffset.UtcNow : DBNull.Value);
        await command.ExecuteNonQueryAsync(Ct);
        return id;
    }

    private static async Task SeedItemAsync(StagingHost host, Guid runId, string code, string reference, long? bank, long? ledger, string? currency)
    {
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO reconciliation_items (id, run_id, code, reference, bank_amount_minor, ledger_amount_minor, currency)
            VALUES (@id, @run, @code, @reference, @bank, @ledger, @currency)
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("reference", reference);
        command.Parameters.AddWithValue("bank", (object?)bank ?? DBNull.Value);
        command.Parameters.AddWithValue("ledger", (object?)ledger ?? DBNull.Value);
        command.Parameters.AddWithValue("currency", (object?)currency ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> RowCountAsync(StagingHost host, string sql)
    {
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>A loopback port that was free a moment ago and has no listener: connections are refused at once.</summary>
    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class RecordingTrigger : IRunTrigger
    {
        private readonly ConcurrentQueue<DateOnly> dates = new();
        internal IReadOnlyCollection<DateOnly> Dates => dates.ToArray();

        public Task TriggerAsync(DateOnly businessDate, CancellationToken ct = default)
        {
            dates.Enqueue(businessDate);
            return Task.CompletedTask;
        }
    }
}
