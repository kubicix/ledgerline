using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Sagas;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Payments.IntegrationTests;

public sealed class TimeoutTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Unanswered_ledger_step_is_resent_with_same_operation_id()
    {
        await using var host = await StartAsync();
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var id = await StartTransferAsync(host, probe);
        await Eventually.ShouldAsync(() => Task.FromResult(Commands(probe, id).Length >= 2));
        var commands = Commands(probe, id).Take(2).ToArray();
        commands[0].MessageId.ShouldNotBe(commands[1].MessageId);
        var payloads = commands.Select(x => (TransferFunds)Ledgerline.BuildingBlocks.Messaging.EnvelopeSerializer.FromPayload(x.Payload, typeof(TransferFunds))).ToArray();
        payloads[0].OperationId.ShouldBe(payloads[1].OperationId);
    }

    [Fact]
    public async Task Ledger_step_moves_to_ManualReview_after_max_attempts()
    {
        await using var host = await StartAsync();
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var id = await StartTransferAsync(host, probe);
        var failed = await probe.WaitForAsync<PaymentFailed>(x => x.PaymentId == id);
        failed.Status.ShouldBe("ManualReview");
        Commands(probe, id).Length.ShouldBe(5);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await db.Payments.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken)).StepAttempt.ShouldBe(5);
        (await db.SagaTimeouts.CountAsync(x => x.PaymentId == id, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Answered_step_deletes_its_timeout_row()
    {
        await using var host = await StartAsync();
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var id = await StartTransferAsync(host, probe);
        var command = await probe.WaitForAsync<TransferFunds>(x => x.PaymentId == id);
        await probe.PublishAsync(new LedgerOperationSucceeded(command.OperationId, id, LedgerOperationKind.TransferFunds, Guid.NewGuid(), DateTimeOffset.UtcNow), id);
        await probe.WaitForAsync<PaymentCompleted>(x => x.PaymentId == id);
        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().SagaTimeouts.CountAsync(x => x.PaymentId == id, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Two_pollers_do_not_resend_the_same_timeout_twice()
    {
        var options = new TimeoutOptions { StepTimeoutMs = 60_000, PollIntervalMs = 50 };
        await using var first = await ServiceHost<Program>.StartAsync(infrastructure, "two_pollers", services => services.AddSingleton(options));
        await using var second = await ServiceHost<Program>.StartOnDatabaseAsync(infrastructure, first.ConnectionString, services => services.AddSingleton(options));
        await using var probe = await PaymentTestSetup.ProbeAsync(first);
        var id = await StartTransferAsync(first, probe);
        await probe.WaitForAsync<TransferFunds>(x => x.PaymentId == id);
        await using (var scope = first.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().SagaTimeouts.Where(x => x.PaymentId == id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.DueAt, DateTimeOffset.UtcNow.AddSeconds(-1)), TestContext.Current.CancellationToken);
        }
        await Eventually.ShouldAsync(() => Task.FromResult(Commands(probe, id).Length >= 2));
        await using var verify = second.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await db.Payments.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken)).StepAttempt.ShouldBe(2);
        (await db.SagaTimeouts.CountAsync(x => x.PaymentId == id, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.Set<OutboxMessage>().CountAsync(x => x.Type == "accounts.command.transfer-funds.v1", TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    private Task<ServiceHost<Program>> StartAsync() => ServiceHost<Program>.StartAsync(infrastructure, "timeout",
        services => services.AddSingleton(new TimeoutOptions { StepTimeoutMs = 300, PollIntervalMs = 50 }));

    private static Ledgerline.BuildingBlocks.Messaging.MessageEnvelope[] Commands(BrokerProbe probe, Guid id) =>
        probe.Messages.Where(x => x.CorrelationId == id && x.Type == "accounts.command.transfer-funds.v1").ToArray();

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
