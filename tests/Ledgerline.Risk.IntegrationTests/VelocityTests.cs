using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Risk;
using Ledgerline.Risk.Features.Evaluate;
using Ledgerline.Risk.Infrastructure;
using Ledgerline.Risk.Infrastructure.Entities;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Ledgerline.Risk.IntegrationTests;

[CollectionDefinition("Redis lifecycle", DisableParallelization = true)]
public sealed class RedisLifecycle;

[Collection("Redis lifecycle")]
public sealed class VelocityTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Redis_recovery_cannot_forget_evaluations_recorded_during_outage()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "risk_recovery");
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "risk.event.#");
        var redis = host.Services.GetRequiredService<IConnectionMultiplexer>();
        var customer = Guid.NewGuid();
        await infrastructure.StopRedisAsync();
        try
        {
            await Eventually.ShouldAsync(() => Task.FromResult(!redis.IsConnected));
            for (var i = 0; i < 9; i++)
            {
                var request = Request(customer);
                await probe.PublishAsync(request, request.PaymentId);
                await probe.WaitForAsync<RiskApproved>(x => x.PaymentId == request.PaymentId);
            }
        }
        finally { await infrastructure.StartRedisAsync(); }
        await host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<Ledgerline.BuildingBlocks.Messaging.Consuming.ConsumerHost<RiskDbContext>>().Single()
            .StopAsync(TestContext.Current.CancellationToken);
        await using var recovered = await ServiceHost<Program>.StartOnDatabaseAsync(infrastructure, host.ConnectionString);
        recovered.Services.GetRequiredService<IConnectionMultiplexer>().IsConnected.ShouldBeTrue();
        var tenth = Request(customer);
        await probe.PublishAsync(tenth, tenth.PaymentId);
        await probe.WaitForAsync<RiskApproved>(x => x.PaymentId == tenth.PaymentId);
        var excess = Request(customer);
        await probe.PublishAsync(excess, excess.PaymentId);
        await probe.WaitForAsync<RiskRejected>(x => x.PaymentId == excess.PaymentId);
    }

    [Fact]
    public async Task Delayed_requests_use_request_time_for_velocity_and_daily_totals()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "risk_delayed");
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "risk.event.#");
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
            await db.Rules.Where(x => x.Code == Ledgerline.Risk.Domain.RuleCode.VelocityLimit)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.MaxCount, 1), TestContext.Current.CancellationToken);
        }
        _ = host.Services.GetRequiredService<IConnectionMultiplexer>();
        await infrastructure.StopRedisAsync();
        try
        {
            var customer = Guid.NewGuid();
            var yesterday = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1).AddHours(23).AddMinutes(59), TimeSpan.Zero);
            var first = Request(customer) with { RequestedAt = yesterday };
            var second = Request(customer) with { RequestedAt = yesterday.AddMinutes(2) };
            await probe.PublishAsync(first, first.PaymentId);
            await probe.WaitForAsync<RiskApproved>(x => x.PaymentId == first.PaymentId);
            await probe.PublishAsync(second, second.PaymentId);
            await probe.WaitForAsync<RiskApproved>(x => x.PaymentId == second.PaymentId);
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
            var totals = await db.DailyTotals.ToArrayAsync(TestContext.Current.CancellationToken);
            totals.Length.ShouldBe(2);
            totals.ShouldAllBe(x => x.TotalMinor == 100);
            var requests = await db.Decisions.Select(x => EF.Property<DateTimeOffset>(x, "RequestedAt")).ToArrayAsync(TestContext.Current.CancellationToken);
            requests.Select(x => DateOnly.FromDateTime(x.UtcDateTime)).Distinct().Count().ShouldBe(2);
        }
        finally { await infrastructure.StartRedisAsync(); }
    }

    [Fact]
    public async Task Database_fallback_serializes_customer_velocity_across_currencies_and_replicas()
    {
        var gate = new CounterGate();
        void Configure(IServiceCollection services) => services.AddScoped<IVelocityCounter>(provider =>
            new GatedCounter(ActivatorUtilities.CreateInstance<FallbackVelocityCounter>(provider), gate));
        await using var first = await ServiceHost<Program>.StartAsync(infrastructure, "risk_currency", Configure);
        await using var second = await ServiceHost<Program>.StartOnDatabaseAsync(infrastructure, first.ConnectionString, Configure);
        await using var probe = await BrokerProbe.CreateAsync(first.Services.GetRequiredService<RabbitMqOptions>().Uri, "risk.event.#");
        var customer = Guid.NewGuid();
        await using (var scope = first.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
            db.Decisions.AddRange(Enumerable.Range(0, 9).Select(_ => new RiskDecision { PaymentId = Guid.NewGuid(),
                CustomerId = customer, Kind = "TopUp", AmountMinor = 1, Currency = "TRY", Outcome = "Approved", EvaluatedAt = DateTimeOffset.UtcNow, RequestedAt = DateTimeOffset.UtcNow }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var firstRedis = first.Services.GetRequiredService<IConnectionMultiplexer>();
        var secondRedis = second.Services.GetRequiredService<IConnectionMultiplexer>();
        await infrastructure.StopRedisAsync();
        try
        {
            await Eventually.ShouldAsync(() => Task.FromResult(!firstRedis.IsConnected && !secondRedis.IsConnected));
            var requests = new[] { Request(customer), Request(customer) with { Currency = "EUR" } };
            await Task.WhenAll(requests.Select(request => probe.PublishAsync(request, request.PaymentId)));
            await Eventually.ShouldAsync(async () =>
            {
                await using var scope = first.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<RiskDbContext>().Decisions.CountAsync() == 11;
            });
            await using var scope = first.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
            (await db.Decisions.CountAsync(x => x.Outcome == "Approved", TestContext.Current.CancellationToken)).ShouldBe(10);
            (await db.Decisions.CountAsync(x => x.Outcome == "Rejected", TestContext.Current.CancellationToken)).ShouldBe(1);
        }
        finally { await infrastructure.StartRedisAsync(); }
    }

    private sealed class CounterGate
    {
        internal int Count;
        internal TaskCompletionSource Both { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class GatedCounter(IVelocityCounter inner, CounterGate gate) : IVelocityCounter
    {
        public async Task<int> CountAsync(Guid customerId, Guid paymentId, DateTimeOffset at, int windowSeconds, CancellationToken ct)
        {
            var count = await inner.CountAsync(customerId, paymentId, at, windowSeconds, ct);
            if (Interlocked.Increment(ref gate.Count) == 2) gate.Both.TrySetResult();
            try { await gate.Both.Task.WaitAsync(TimeSpan.FromMilliseconds(500), ct); }
            catch (TimeoutException) { }
            return count;
        }
    }

    [Fact]
    public async Task Velocity_counts_distinct_payments_within_window()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(infrastructure.RedisConnectionString);
        var counter = new RedisVelocityCounter(redis);
        var customer = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        (await counter.CountAsync(customer, Guid.NewGuid(), at, 60, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await counter.CountAsync(customer, Guid.NewGuid(), at.AddSeconds(1), 60, TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task Velocity_ignores_same_payment_added_twice()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(infrastructure.RedisConnectionString);
        var counter = new RedisVelocityCounter(redis);
        var customer = Guid.NewGuid();
        var payment = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        (await counter.CountAsync(customer, payment, at, 60, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await counter.CountAsync(customer, payment, at.AddSeconds(1), 60, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task Velocity_forgets_entries_older_than_window()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(infrastructure.RedisConnectionString);
        var counter = new RedisVelocityCounter(redis);
        var customer = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await counter.CountAsync(customer, Guid.NewGuid(), at, 60, TestContext.Current.CancellationToken);
        (await counter.CountAsync(customer, Guid.NewGuid(), at.AddSeconds(59), 60, TestContext.Current.CancellationToken)).ShouldBe(2);
        (await counter.CountAsync(customer, Guid.NewGuid(), at.AddSeconds(61), 60, TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task Redis_down_velocity_falls_back_to_database()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "risk_fallback");
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "risk.event.#");
        var customer = Guid.NewGuid();
        var first = Request(customer);
        await probe.PublishAsync(first, first.PaymentId);
        await probe.WaitForAsync<RiskApproved>(x => x.PaymentId == first.PaymentId);
        await infrastructure.StopRedisAsync();
        try
        {
            for (var i = 0; i < 10; i++)
            {
                var request = Request(customer);
                await probe.PublishAsync(request, request.PaymentId);
            }
            await Eventually.ShouldAsync(async () =>
            {
                await using var scope = host.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<RiskDbContext>().Decisions.CountAsync() == 11;
            }, TimeSpan.FromSeconds(90));
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
            (await db.Decisions.CountAsync(x => x.Outcome == "Approved", TestContext.Current.CancellationToken)).ShouldBe(10);
            (await db.Decisions.SingleAsync(x => x.Outcome == "Rejected", TestContext.Current.CancellationToken)).RuleCodes.ShouldBe(["VelocityLimit"]);
        }
        finally { await infrastructure.StartRedisAsync(); }
    }

    private static EvaluateRisk Request(Guid customer) => new(Guid.NewGuid(), "Transfer", customer,
        Guid.NewGuid(), 100, "TRY", DateTimeOffset.UtcNow);
}
