using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Payments.IntegrationTests;

public sealed class StatusOrderingTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Older_status_events_cannot_regress_newer_state_across_replicas()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var replica = await ServiceHost<Program>.StartOnDatabaseAsync(infrastructure, host.ConnectionString);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var account = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        await SendAsync(probe, account, 30, "Active");
        await WaitForInboxAsync(host, 2);
        await Task.WhenAll(Enumerable.Range(2, 28).Select(version => SendAsync(probe, account, version, "Frozen")));
        await WaitForInboxAsync(host, 30);
        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().AccountRefs.SingleAsync(TestContext.Current.CancellationToken)).Status.ShouldBe("Active");
    }

    private static Task SendAsync(BrokerProbe probe, Guid account, int version, string status) => probe.PublishRawAsync(
        new MessageEnvelope(Guid.NewGuid(), "accounts.event.account-status-changed.v1", DateTimeOffset.UtcNow, account, null, null,
            EnvelopeSerializer.ToPayload(new { accountId = account, status, changedAt = DateTimeOffset.UtcNow, version })));
    private static Task WaitForInboxAsync(ServiceHost<Program> host, int count) => Eventually.ShouldAsync(async () =>
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<InboxMessage>().CountAsync() == count;
    });
}
