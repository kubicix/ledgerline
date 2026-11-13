using System.Net;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Ledgerline.Payments.IntegrationTests;

[CollectionDefinition("Redis lifecycle", DisableParallelization = true)]
public sealed class RedisLifecycle;

[Collection("Redis lifecycle")]
public sealed class IdempotencyTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Second_request_while_first_in_progress_returns_409_with_retry_after()
    {
        var gate = new TransitionGate();
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "idem_gate", services => services.AddSingleton<ICrashPoint>(gate));
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        using var client = host.CreateClient(owner);
        var first = PaymentTestSetup.TransferAsync(client, from, to, key: "inflight");
        try
        {
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            using var second = await PaymentTestSetup.TransferAsync(client, from, to, key: "inflight");
            second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            second.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
            (await PaymentTestSetup.BodyAsync(second)).GetProperty("type").GetString().ShouldBe("urn:ledgerline:problem:request-in-progress");
        }
        finally { gate.Release(); }
        using var completed = await first;
        completed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await AssertSinglePaymentAsync(host);
    }

    [Fact]
    public async Task Replay_after_completion_is_served_and_returns_same_id()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        using var client = host.CreateClient(owner);
        using var first = await PaymentTestSetup.TransferAsync(client, from, to, key: "done");
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var id = (await PaymentTestSetup.BodyAsync(first)).GetProperty("id").GetGuid();
        using var redis = await ConnectionMultiplexer.ConnectAsync(infrastructure.RedisConnectionString);
        var cached = await redis.GetDatabase().StringGetAsync($"idem:payments:{owner}:done");
        cached.ToString().ShouldStartWith("done:");
        cached.ToString().ShouldEndWith(":" + id);
        using var replay = await PaymentTestSetup.TransferAsync(client, from, to, key: "done");
        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await PaymentTestSetup.BodyAsync(replay)).GetProperty("id").GetGuid().ShouldBe(id);
        await AssertSinglePaymentAsync(host);
    }

    [Fact]
    public async Task Same_key_different_body_returns_422_from_redis_path()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        using var client = host.CreateClient(owner);
        using var first = await PaymentTestSetup.TransferAsync(client, from, to, key: "different");
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var redis = await ConnectionMultiplexer.ConnectAsync(infrastructure.RedisConnectionString);
        (await redis.GetDatabase().KeyExistsAsync($"idem:payments:{owner}:different")).ShouldBeTrue();
        using var different = await PaymentTestSetup.TransferAsync(client, from, to, 5000, "different");
        different.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PaymentTestSetup.BodyAsync(different)).GetProperty("type").GetString().ShouldBe("urn:ledgerline:problem:idempotency-key-reused");
        await AssertSinglePaymentAsync(host);
    }

    [Fact]
    public async Task Redis_down_idempotency_falls_back_to_database()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        using var client = host.CreateClient(owner);
        await infrastructure.StopRedisAsync();
        try
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PaymentTestSetup.TransferAsync(client, from, to, key: "offline")));
            try
            {
                responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Accepted);
                var bodies = await Task.WhenAll(responses.Select(PaymentTestSetup.BodyAsync));
                bodies.Select(body => body.GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(1);
                await AssertSinglePaymentAsync(host);
            }
            finally { foreach (var response in responses) response.Dispose(); }
        }
        finally { await infrastructure.StartRedisAsync(); }
    }

    [Fact]
    public async Task Failed_request_releases_the_key()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        using var client = host.CreateClient(owner);
        using var invalid = await PaymentTestSetup.TransferAsync(client, from, to, 0, "corrected");
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var redis = await ConnectionMultiplexer.ConnectAsync(infrastructure.RedisConnectionString);
        (await redis.GetDatabase().KeyExistsAsync($"idem:payments:{owner}:corrected")).ShouldBeFalse();
        using var corrected = await PaymentTestSetup.TransferAsync(client, from, to, key: "corrected");
        corrected.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await AssertSinglePaymentAsync(host);
    }

    private static async Task AssertSinglePaymentAsync(ServiceHost<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Payments.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    public sealed class TransitionGate : ICrashPoint
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => entered.Task;
        public void Release() => released.TrySetResult();
        public void Hit(string name)
        {
            if (name != "saga.after-transition-commit") return;
            entered.TrySetResult();
            released.Task.GetAwaiter().GetResult();
        }
    }
}
