using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Sagas;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Payments.IntegrationTests;

[Collection("Redis lifecycle")]
public sealed class RefundFlowTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Refund_recovers_after_five_consumer_failures_without_freeing_pending_capacity()
    {
        var failure = new RefundFailure();
        await using var scenario = await MerchantScenario.StartAsync(infrastructure, accountOverrides: services =>
        {
            services.AddSingleton(new Ledgerline.BuildingBlocks.Messaging.Consuming.ConsumerOptions { Service = "accounts", MaxAttempts = 5, RetryTtlMs = 20 });
            services.AddScoped<Ledgerline.Accounts.Ledger.ILedger>(provider => new FailingLedger(
                ActivatorUtilities.CreateInstance<Ledgerline.Accounts.Ledger.Ledger>(provider), failure));
        }, paymentOverrides: services => services.AddSingleton(new TimeoutOptions { StepTimeoutMs = 500, PollIntervalMs = 20 }));
        var payment = await CaptureAsync(scenario);
        failure.Broken = true;
        using var response = await RefundAsync(scenario, payment, 1500);
        var id = (await PaymentTestSetup.BodyAsync(response)).GetProperty("id").GetGuid();
        await Eventually.ShouldAsync(() => Task.FromResult(Volatile.Read(ref failure.Attempts) >= 5));
        using var exceeds = await RefundAsync(scenario, payment, 1500);
        exceeds.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        failure.Broken = false;
        await scenario.Probe.WaitForAsync<PaymentRefunded>(x => x.RefundId == id);
        (await scenario.PaymentAsync(payment)).RefundedMinor.ShouldBe(1500);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    private sealed class RefundFailure
    {
        internal volatile bool Broken;
        internal int Attempts;
    }
    private sealed class FailingLedger(Ledgerline.Accounts.Ledger.ILedger inner, RefundFailure failure) : Ledgerline.Accounts.Ledger.ILedger
    {
        public Task<Ledgerline.Accounts.Ledger.PostingResult> PostAsync(Ledgerline.Accounts.Ledger.PostingRequest request, CancellationToken ct)
        {
            if (failure.Broken && request.EntryType == Ledgerline.Accounts.Ledger.EntryType.Refund)
            {
                Interlocked.Increment(ref failure.Attempts);
                throw new InvalidOperationException("Simulated temporary ledger persistence failure.");
            }
            return inner.PostAsync(request, ct);
        }
    }

    [Fact]
    public async Task Partial_refund_completes_and_updates_refunded_minor()
    {
        await using var scenario = await MerchantScenario.StartAsync(infrastructure);
        var payment = await CaptureAsync(scenario);
        using var response = await RefundAsync(scenario, payment, 500);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var id = (await PaymentTestSetup.BodyAsync(response)).GetProperty("id").GetGuid();
        var refunded = await scenario.Probe.WaitForAsync<PaymentRefunded>(x => x.RefundId == id);
        refunded.RefundAmountMinor.ShouldBe(500);
        (await scenario.PaymentAsync(payment)).RefundedMinor.ShouldBe(500);
        await using var scope = scenario.Host.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().SagaSteps
            .Where(x => x.PaymentId == payment && x.Outcome.StartsWith("Refund")).Select(x => x.Outcome).ToListAsync(TestContext.Current.CancellationToken);
        audit.ShouldBe(["RefundStarted", "RefundCompleted"], ignoreOrder: true);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Refund_list_is_owned_and_paginates()
    {
        await using var scenario = await MerchantScenario.StartAsync(infrastructure);
        var payment = await CaptureAsync(scenario);
        for (var i = 0; i < 2; i++)
        {
            using var response = await RefundAsync(scenario, payment, 500);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            var id = (await PaymentTestSetup.BodyAsync(response)).GetProperty("id").GetGuid();
            await scenario.Probe.WaitForAsync<PaymentRefunded>(x => x.RefundId == id);
        }
        using var owner = scenario.Host.CreateClient(scenario.Owner);
        var first = await owner.GetFromJsonAsync<JsonElement>($"/payments/{payment}/refunds?limit=1", TestContext.Current.CancellationToken);
        var cursor = first.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();
        var second = await owner.GetFromJsonAsync<JsonElement>($"/payments/{payment}/refunds?limit=1&cursor={Uri.EscapeDataString(cursor!)}", TestContext.Current.CancellationToken);
        first.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldNotBe(second.GetProperty("items")[0].GetProperty("id").GetGuid());
        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        using var stranger = scenario.Host.CreateClient(Guid.NewGuid(), "merchant");
        using var denied = await stranger.GetAsync($"/payments/{payment}/refunds", TestContext.Current.CancellationToken);
        denied.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Refunds_cannot_exceed_captured_amount_including_pending()
    {
        var gate = new DeliveryGate();
        await using var scenario = await MerchantScenario.StartAsync(infrastructure,
            accountOverrides: services => services.AddSingleton<ICrashPoint>(gate));
        var payment = await CaptureAsync(scenario);
        gate.Activate();
        try
        {
            var responses = await Task.WhenAll(RefundAsync(scenario, payment, 1500), RefundAsync(scenario, payment, 1500));
            try
            {
                responses.Count(x => x.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
                responses.Count(x => x.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
            }
            finally { foreach (var response in responses) response.Dispose(); }
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await using var scope = scenario.Host.Services.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Refunds.CountAsync(x => x.Status == RefundStatus.Processing, TestContext.Current.CancellationToken)).ShouldBe(1);
        }
        finally { gate.Release(); }
        await scenario.Probe.WaitForAsync<PaymentRefunded>(x => x.PaymentId == payment);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Rejected_refund_is_Failed_and_frees_refundable_amount()
    {
        await using var scenario = await MerchantScenario.StartAsync(infrastructure);
        var payment = await CaptureAsync(scenario);
        await scenario.SetMerchantStatusAsync("freeze");
        using var rejected = await RefundAsync(scenario, payment, 1500);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var rejectedId = (await PaymentTestSetup.BodyAsync(rejected)).GetProperty("id").GetGuid();
        await Eventually.ShouldAsync(async () =>
        {
            await using var scope = scenario.Host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Refunds.AnyAsync(x => x.Id == rejectedId && x.Status == RefundStatus.Failed);
        });
        await scenario.SetMerchantStatusAsync("unfreeze");
        using var retry = await RefundAsync(scenario, payment, 2500);
        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var retryId = (await PaymentTestSetup.BodyAsync(retry)).GetProperty("id").GetGuid();
        await scenario.Probe.WaitForAsync<PaymentRefunded>(x => x.RefundId == retryId);
        (await scenario.PaymentAsync(payment)).RefundedMinor.ShouldBe(2500);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Refund_of_uncaptured_payment_returns_409()
    {
        await using var scenario = await MerchantScenario.StartAsync(infrastructure);
        var payment = await scenario.AuthorizeAsync();
        using var response = await RefundAsync(scenario, payment, 500);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Refund_is_idempotent_by_key()
    {
        await using var scenario = await MerchantScenario.StartAsync(infrastructure);
        var payment = await CaptureAsync(scenario);
        using var first = await RefundAsync(scenario, payment, 500, "refund-once");
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var id = (await PaymentTestSetup.BodyAsync(first)).GetProperty("id").GetGuid();
        await scenario.Probe.WaitForAsync<PaymentRefunded>(x => x.RefundId == id);
        using var replay = await RefundAsync(scenario, payment, 500, "refund-once");
        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await PaymentTestSetup.BodyAsync(replay);
        body.GetProperty("id").GetGuid().ShouldBe(id);
        body.GetProperty("status").GetString().ShouldBe("Completed");
        var duplicate = Guid.NewGuid();
        await scenario.Probe.PublishAsync(new LedgerOperationSucceeded(OperationIds.ForRefund(id), payment,
            LedgerOperationKind.RefundFunds, Guid.NewGuid(), DateTimeOffset.UtcNow), payment, duplicate);
        await Eventually.ShouldAsync(async () =>
        {
            await using var scope = scenario.Host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<InboxMessage>().AnyAsync(x => x.MessageId == duplicate);
        });
        await using var verification = scenario.Host.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await db.Refunds.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.Set<OutboxMessage>().CountAsync(x => x.Type == "payments.event.payment-refunded.v1", TestContext.Current.CancellationToken)).ShouldBe(1);
        (await scenario.PaymentAsync(payment)).RefundedMinor.ShouldBe(500);
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    [Fact]
    public async Task Refund_and_capture_share_key_scope_when_Redis_is_down()
    {
        await using var scenario = await MerchantScenario.StartAsync(infrastructure);
        var captured = await CaptureAsync(scenario);
        var authorized = await scenario.AuthorizeAsync();
        await infrastructure.StopRedisAsync();
        try
        {
            using var refund = await RefundAsync(scenario, captured, 500, "shared-action");
            refund.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            await scenario.Probe.WaitForAsync<PaymentRefunded>(x => x.PaymentId == captured);
            using var capture = await scenario.ActionAsync(authorized, "capture", 500, "shared-action");
            capture.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }
        finally { await infrastructure.StartRedisAsync(); }
        await LedgerAssert.InvariantsHoldAsync(scenario.Accounts.ConnectionString);
    }

    private static async Task<Guid> CaptureAsync(MerchantScenario scenario)
    {
        var id = await scenario.AuthorizeAsync();
        using var response = await scenario.ActionAsync(id, "capture");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await scenario.Probe.WaitForAsync<PaymentCaptured>(x => x.PaymentId == id);
        return id;
    }

    private static async Task<HttpResponseMessage> RefundAsync(MerchantScenario scenario, Guid payment, long amount, string? key = null)
    {
        using var client = scenario.Host.CreateClient(scenario.MerchantOwner, "merchant");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/payments/{payment}/refunds") { Content = JsonContent.Create(new { amountMinor = amount }) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public sealed class DeliveryGate : ICrashPoint
    {
        private int active;
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => entered.Task;
        public void Activate() => Interlocked.Exchange(ref active, 1);
        public void Release() { Interlocked.Exchange(ref active, 0); released.TrySetResult(); }
        public void Hit(string name)
        {
            if (name != "consumer.before-delivery" || Volatile.Read(ref active) == 0) return;
            entered.TrySetResult();
            released.Task.GetAwaiter().GetResult();
        }
    }
}
