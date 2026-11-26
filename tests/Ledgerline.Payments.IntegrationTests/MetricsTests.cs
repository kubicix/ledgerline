using System.Diagnostics.Metrics;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Sagas;
using Ledgerline.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Payments.IntegrationTests;

/// <summary>Every host has its own meter factory; the recorder listens to that scope only, so parallel Payments tests do not mix in.</summary>
public sealed class MetricsTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Saga_duration_is_recorded_with_kind_and_outcome_tags()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        using var metrics = MetricRecorder.ForMeter("Ledgerline", host.Services.GetRequiredService<IMeterFactory>());
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var id = await StartTransferAsync(host, probe);
        var command = await probe.WaitForAsync<TransferFunds>(x => x.PaymentId == id);
        await probe.PublishAsync(new LedgerOperationSucceeded(command.OperationId, id, LedgerOperationKind.TransferFunds, Guid.NewGuid(), DateTimeOffset.UtcNow), id);
        await probe.WaitForAsync<PaymentCompleted>(x => x.PaymentId == id);

        await Eventually.ShouldAsync(() => Task.FromResult(metrics.Of("ledgerline.saga.duration").Length == 1),
            because: "Expected one duration for the finished payment");
        var duration = metrics.Of("ledgerline.saga.duration").Single();
        duration.Unit.ShouldBe("s");
        duration.Tag("kind").ShouldBe(nameof(PaymentKind.Transfer));
        duration.Tag("outcome").ShouldBe(nameof(PaymentStatus.Completed));
        ((double)duration.Value).ShouldBeInRange(0, 60);
        metrics.Of("ledgerline.saga.manual_review").ShouldBeEmpty();
    }

    [Fact]
    public async Task Manual_review_increments_counter()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "metrics_review",
            services => services.AddSingleton(new TimeoutOptions { StepTimeoutMs = 300, PollIntervalMs = 50 }));
        using var metrics = MetricRecorder.ForMeter("Ledgerline", host.Services.GetRequiredService<IMeterFactory>());
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var id = await StartTransferAsync(host, probe);
        (await probe.WaitForAsync<PaymentFailed>(x => x.PaymentId == id)).Status.ShouldBe(nameof(PaymentStatus.ManualReview));

        await Eventually.ShouldAsync(() => Task.FromResult(metrics.Of("ledgerline.saga.manual_review").Sum(m => (long)m.Value) == 1),
            because: "Expected one manual review");
        metrics.Of("ledgerline.saga.duration").Single().Tag("outcome").ShouldBe(nameof(PaymentStatus.ManualReview));
    }

    private static async Task<Guid> StartTransferAsync(ServiceHost<Program> host, BrokerProbe probe)
    {
        var owner = Guid.NewGuid();
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        using var client = host.CreateClient(owner);
        using var response = await PaymentTestSetup.TransferAsync(client, from, to);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        return (await PaymentTestSetup.BodyAsync(response)).GetProperty("id").GetGuid();
    }
}
