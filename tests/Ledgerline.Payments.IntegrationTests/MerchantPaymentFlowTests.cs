using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Sagas;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using AccountsDb = Ledgerline.Accounts.Infrastructure.AccountsDbContext;

namespace Ledgerline.Payments.IntegrationTests;

[Collection("Redis lifecycle")]
public sealed class MerchantPaymentFlowTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Manual_payment_reaches_Authorized_with_expiry()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.AuthorizeAsync();
        var payment = await scenario.PaymentAsync(id);
        payment.Status.ShouldBe(PaymentStatus.Authorized);
        payment.AuthorizationExpiresAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(6));
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Capture_by_merchant_reaches_Captured_with_amount()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.AuthorizeAsync();
        using var response = await scenario.ActionAsync(id, "capture");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await scenario.Probe.WaitForAsync<PaymentCaptured>(x => x.PaymentId == id);
        (await scenario.PaymentAsync(id)).CapturedMinor.ShouldBe(2500);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Partial_capture_sets_captured_minor()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.AuthorizeAsync();
        using var response = await scenario.ActionAsync(id, "capture", 1000);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await scenario.Probe.WaitForAsync<PaymentCaptured>(x => x.PaymentId == id);
        (await scenario.PaymentAsync(id)).CapturedMinor.ShouldBe(1000);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Capture_by_non_owner_returns_404()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.AuthorizeAsync();
        using var stranger = scenario.Host.CreateClient(Guid.NewGuid(), "merchant");
        using var request = ActionRequest(id, "capture", null, "stranger");
        using var response = await stranger.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Capture_twice_returns_409_second_time()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.AuthorizeAsync();
        using var first = await scenario.ActionAsync(id, "capture");
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await scenario.Probe.WaitForAsync<PaymentCaptured>(x => x.PaymentId == id);
        using var second = await scenario.ActionAsync(id, "capture");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Capture_and_void_race_has_exactly_one_winner()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.AuthorizeAsync();
        var responses = await Task.WhenAll(scenario.ActionAsync(id, "capture"), scenario.ActionAsync(id, "void"));
        try
        {
            responses.Count(x => x.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
            responses.Count(x => x.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
            await Eventually.ShouldAsync(async () => (await scenario.PaymentAsync(id)).Status is PaymentStatus.Captured or PaymentStatus.Voided);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Void_reaches_Voided()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.AuthorizeAsync();
        using var response = await scenario.ActionAsync(id, "void");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await scenario.Probe.WaitForAsync<PaymentVoided>(x => x.PaymentId == id)).Reason.ShouldBe("Requested");
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Authorization_expiry_releases_hold()
    {
        await using var scenario = await SetupAsync(TimeSpan.FromMilliseconds(500));
        var id = await scenario.AuthorizeAsync();
        (await scenario.Probe.WaitForAsync<PaymentVoided>(x => x.PaymentId == id)).Reason.ShouldBe("Expired");
        await using var scope = scenario.Accounts.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AccountsDb>().Holds.SingleAsync(x => x.PaymentId == id, TestContext.Current.CancellationToken)).Status.ShouldBe("Released");
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Automatic_capture_completes_without_merchant_call()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.CreateAsync("Automatic");
        await scenario.ApproveAsync(id);
        (await scenario.Probe.WaitForAsync<PaymentCaptured>(x => x.PaymentId == id)).CapturedMinor.ShouldBe(2500);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Rejected_capture_returns_payment_to_Authorized_with_reason()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.AuthorizeAsync();
        await scenario.SetMerchantStatusAsync("freeze");
        using var response = await scenario.ActionAsync(id, "capture");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Eventually.ShouldAsync(async () => (await scenario.PaymentAsync(id)) is { Status: PaymentStatus.Authorized, FailureReason: "AccountFrozen" });
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task List_payments_filters_by_account_and_status()
    {
        await using var scenario = await SetupAsync();
        var authorized = await scenario.AuthorizeAsync();
        var captured = await scenario.AuthorizeAsync();
        using var capture = await scenario.ActionAsync(captured, "capture");
        capture.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await scenario.Probe.WaitForAsync<PaymentCaptured>(x => x.PaymentId == captured);
        using var merchant = scenario.Host.CreateClient(scenario.MerchantOwner, "merchant");
        var page = await merchant.GetFromJsonAsync<JsonElement>($"/payments?accountId={scenario.Merchant}&status=Authorized&limit=1", TestContext.Current.CancellationToken);
        page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(authorized);
        page.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        var first = await merchant.GetFromJsonAsync<JsonElement>($"/payments?accountId={scenario.Merchant}&limit=1", TestContext.Current.CancellationToken);
        var cursor = first.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();
        var second = await merchant.GetFromJsonAsync<JsonElement>($"/payments?accountId={scenario.Merchant}&limit=1&cursor={Uri.EscapeDataString(cursor!)}", TestContext.Current.CancellationToken);
        var ids = new[] { first, second }.Select(x => x.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid()).ToArray();
        ids.ShouldBe([authorized, captured], ignoreOrder: true);
        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Concurrent_actions_on_different_payments_cannot_reuse_one_key_without_Redis()
    {
        await using var scenario = await SetupAsync();
        var payments = new List<Guid>();
        for (var i = 0; i < 4; i++) payments.Add(await scenario.AuthorizeAsync());
        await infrastructure.StopRedisAsync();
        try
        {
            var responses = await Task.WhenAll(payments.Select(id => scenario.ActionAsync(id, "capture", 100, "shared-key")));
            try
            {
                responses.Count(x => x.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
                responses.Count(x => x.StatusCode == HttpStatusCode.UnprocessableEntity).ShouldBe(3);
            }
            finally { foreach (var response in responses) response.Dispose(); }
            await Eventually.ShouldAsync(async () =>
            {
                await using var scope = scenario.Host.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Payments.CountAsync(x => x.Status == PaymentStatus.Capturing) == 0;
            });
        }
        finally { await infrastructure.StartRedisAsync(); }
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Capture_replay_and_hash_check_use_database_when_Redis_is_down()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.AuthorizeAsync();
        using var first = await scenario.ActionAsync(id, "capture", 1000, "capture-once");
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await scenario.Probe.WaitForAsync<PaymentCaptured>(x => x.PaymentId == id);
        await infrastructure.StopRedisAsync();
        try
        {
            using var replay = await scenario.ActionAsync(id, "capture", 1000, "capture-once");
            replay.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await PaymentTestSetup.BodyAsync(replay)).GetProperty("status").GetString().ShouldBe("Captured");
            using var changed = await scenario.ActionAsync(id, "capture", 500, "capture-once");
            changed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }
        finally { await infrastructure.StartRedisAsync(); }
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task New_capture_request_after_rejection_uses_a_new_operation_and_can_succeed()
    {
        await using var scenario = await SetupAsync();
        var id = await scenario.AuthorizeAsync();
        await scenario.SetMerchantStatusAsync("freeze");
        using var rejected = await scenario.ActionAsync(id, "capture", 1000, "attempt-one");
        rejected.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Eventually.ShouldAsync(async () => (await scenario.PaymentAsync(id)) is { Status: PaymentStatus.Authorized, FailureReason: "AccountFrozen" });
        await scenario.SetMerchantStatusAsync("unfreeze");
        using var retried = await scenario.ActionAsync(id, "capture", 1000, "attempt-two");
        retried.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await scenario.Probe.WaitForAsync<PaymentCaptured>(x => x.PaymentId == id);
        await using var scope = scenario.Accounts.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AccountsDb>().LedgerOperations.CountAsync(x => x.PaymentId == id && x.Kind == "CaptureFunds", TestContext.Current.CancellationToken)).ShouldBe(2);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Capture_and_expiry_use_consistent_lock_order()
    {
        var gate = new TimeoutGate();
        await using var scenario = await SetupAsync(TimeSpan.FromMilliseconds(500), gate);
        var id = await scenario.AuthorizeAsync();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var capture = scenario.ActionAsync(id, "capture");
        try
        {
            await Eventually.ShouldAsync(async () =>
            {
                await using var connection = new NpgsqlConnection(scenario.Host.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND state='active' AND wait_event_type='Lock'", connection);
                return (long)(await command.ExecuteScalarAsync())! > 0;
            });
        }
        finally { gate.Release(); }
        using var response = await capture;
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Accepted, HttpStatusCode.Conflict);
        await Eventually.ShouldAsync(async () => (await scenario.PaymentAsync(id)).Status is PaymentStatus.Captured or PaymentStatus.Voided);
        gate.Log.HadDeadlock.ShouldBeFalse();
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    private Task<MerchantScenario> SetupAsync(TimeSpan? ttl = null, TimeoutGate? gate = null) =>
        MerchantScenario.StartAsync(infrastructure, ttl, paymentOverrides: services =>
        {
            if (gate is null) return;
            services.AddSingleton<ICrashPoint>(gate);
            services.AddSingleton<ILogger<SagaTimeoutPoller>>(gate.Log);
        });

    public sealed class TimeoutGate : ICrashPoint
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PollerLog Log { get; } = new();
        public Task Entered => entered.Task;
        public void Release() => released.TrySetResult();
        public void Hit(string name)
        {
            if (name != "saga.timeout-locked") return;
            entered.TrySetResult();
            released.Task.GetAwaiter().GetResult();
        }
    }

    public sealed class PollerLog : ILogger<SagaTimeoutPoller>
    {
        private int deadlock;
        public bool HadDeadlock => Volatile.Read(ref deadlock) != 0;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            for (var current = exception; current is not null; current = current.InnerException)
                if (current is PostgresException { SqlState: "40P01" }) Interlocked.Exchange(ref deadlock, 1);
        }
    }

    private static HttpRequestMessage ActionRequest(Guid id, string action, long? amount, string key) =>
        MerchantScenario.ActionRequest(id, action, amount, key);

}
