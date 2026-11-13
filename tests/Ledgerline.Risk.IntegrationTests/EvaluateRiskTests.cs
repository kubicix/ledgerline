using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Risk;
using Ledgerline.Risk.Domain;
using Ledgerline.Risk.Infrastructure;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[assembly: AssemblyFixture(typeof(InfrastructureFixture))]

namespace Ledgerline.Risk.IntegrationTests;

public sealed class EvaluateRiskTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Approved_request_publishes_RiskApproved_and_adds_to_daily_total()
    {
        await using var host = await StartAsync();
        await using var probe = await ProbeAsync(host);
        var request = Request(100);
        await probe.PublishAsync(request, request.PaymentId);
        await probe.WaitForAsync<RiskApproved>(x => x.PaymentId == request.PaymentId);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
        (await db.Decisions.SingleAsync(TestContext.Current.CancellationToken)).Outcome.ShouldBe("Approved");
        (await db.DailyTotals.SingleAsync(TestContext.Current.CancellationToken)).TotalMinor.ShouldBe(100);
    }

    [Fact]
    public async Task Rejected_request_publishes_rule_codes_and_does_not_add_to_total()
    {
        await using var host = await StartAsync();
        await using var probe = await ProbeAsync(host);
        var request = Request(5_000_001);
        await probe.PublishAsync(request, request.PaymentId);
        var reply = await probe.WaitForAsync<RiskRejected>(x => x.PaymentId == request.PaymentId);
        reply.RuleCodes.ShouldBe(["MaxSingleAmount"]);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
        (await db.DailyTotals.SingleAsync(TestContext.Current.CancellationToken)).TotalMinor.ShouldBe(0);
    }

    [Fact]
    public async Task Evaluating_same_payment_twice_counts_once()
    {
        await using var host = await StartAsync();
        await using var probe = await ProbeAsync(host);
        var request = Request(100);
        await probe.PublishAsync(request, request.PaymentId);
        var first = await probe.WaitForAsync<RiskApproved>(x => x.PaymentId == request.PaymentId);
        await probe.PublishAsync(request, request.PaymentId);
        await Eventually.ShouldAsync(() => Task.FromResult(probe.Messages.Count(x => x.Type == "risk.event.risk-approved.v1") == 2));
        var replies = probe.Messages.Where(x => x.Type == "risk.event.risk-approved.v1")
            .Select(x => (RiskApproved)Ledgerline.BuildingBlocks.Messaging.EnvelopeSerializer.FromPayload(x.Payload, typeof(RiskApproved))).ToArray();
        replies.ShouldAllBe(x => x == first);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
        (await db.Decisions.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.DailyTotals.SingleAsync(TestContext.Current.CancellationToken)).TotalMinor.ShouldBe(100);
    }

    [Fact]
    public async Task Concurrent_evaluations_respect_daily_limit()
    {
        await using var host = await StartAsync();
        await using var replica = await ServiceHost<Program>.StartOnDatabaseAsync(infrastructure, host.ConnectionString);
        await ConfigureDailyLimitAsync(host, 300);
        await using var probe = await ProbeAsync(host);
        var customer = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
        {
            var request = Request(100, customer);
            return probe.PublishAsync(request, request.PaymentId);
        }));
        await WaitForDecisionsAsync(host, 10);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
        (await db.Decisions.CountAsync(x => x.Outcome == "Approved", TestContext.Current.CancellationToken)).ShouldBe(3);
        (await db.DailyTotals.SingleAsync(TestContext.Current.CancellationToken)).TotalMinor.ShouldBe(300);
    }

    [Fact]
    public async Task Daily_total_equals_sum_of_approved_decisions()
    {
        await using var host = await StartAsync();
        await ConfigureDailyLimitAsync(host, 2_000);
        await using var probe = await ProbeAsync(host);
        var random = new Random(731);
        var customer = Guid.NewGuid();
        for (var i = 0; i < 50; i++)
        {
            var request = Request(random.Next(1, 200), customer);
            await probe.PublishAsync(request, request.PaymentId);
        }
        await WaitForDecisionsAsync(host, 50);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
        var approved = await db.Decisions.Where(x => x.Outcome == "Approved").SumAsync(x => x.AmountMinor, TestContext.Current.CancellationToken);
        (await db.DailyTotals.SingleAsync(TestContext.Current.CancellationToken)).TotalMinor.ShouldBe(approved);
        approved.ShouldBeLessThanOrEqualTo(2_000);
    }

    private Task<ServiceHost<Program>> StartAsync() => ServiceHost<Program>.StartAsync(infrastructure, "risk_eval");
    private static Task<BrokerProbe> ProbeAsync(ServiceHost<Program> host) =>
        BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "risk.event.#");
    private static EvaluateRisk Request(long amount, Guid? customer = null) =>
        new(Guid.NewGuid(), "TopUp", customer ?? Guid.NewGuid(), Guid.NewGuid(), amount, "TRY", DateTimeOffset.UtcNow);

    private static async Task ConfigureDailyLimitAsync(ServiceHost<Program> host, long threshold)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
        await db.Rules.ExecuteUpdateAsync(update => update.SetProperty(x => x.Enabled, false), TestContext.Current.CancellationToken);
        await db.Rules.Where(x => x.Code == RuleCode.DailyAmountLimit && x.Currency == "TRY")
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Enabled, true).SetProperty(x => x.ThresholdMinor, threshold), TestContext.Current.CancellationToken);
    }

    private static Task WaitForDecisionsAsync(ServiceHost<Program> host, int count) => Eventually.ShouldAsync(async () =>
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RiskDbContext>().Decisions.CountAsync() == count;
    }, TimeSpan.FromSeconds(20));
}
