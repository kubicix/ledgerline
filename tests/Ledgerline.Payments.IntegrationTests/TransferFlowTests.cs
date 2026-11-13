using System.Net;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Payments.IntegrationTests;

public sealed class TransferFlowTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task AccountOpened_event_creates_account_ref()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var id = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        await using var scope = host.Services.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().AccountRefs.SingleAsync(x => x.AccountId == id, TestContext.Current.CancellationToken);
        account.OwnerId.ShouldBe(owner);
        account.Type.ShouldBe("CustomerWallet");
        account.Currency.ShouldBe("TRY");
        account.Status.ShouldBe("Active");
    }

    [Fact]
    public async Task Transfer_completes_when_ledger_succeeds()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var (owner, id) = await StartTransferAsync(host, probe);
        var command = await probe.WaitForAsync<TransferFunds>(x => x.PaymentId == id);
        await probe.PublishAsync(new LedgerOperationSucceeded(command.OperationId, id, LedgerOperationKind.TransferFunds, Guid.NewGuid(), DateTimeOffset.UtcNow), id);
        (await probe.WaitForAsync<PaymentCompleted>(x => x.PaymentId == id)).AmountMinor.ShouldBe(2500);
        using var client = host.CreateClient(owner);
        using var response = await client.GetAsync("/payments/" + id, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await PaymentTestSetup.BodyAsync(response);
        body.GetProperty("status").GetString().ShouldBe("Completed");
        body.GetProperty("timeline").GetArrayLength().ShouldBe(2);
        using var foreign = host.CreateClient(Guid.NewGuid());
        using var hidden = await foreign.GetAsync("/payments/" + id, TestContext.Current.CancellationToken);
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Transfer_fails_when_ledger_rejects()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var (_, id) = await StartTransferAsync(host, probe);
        var command = await probe.WaitForAsync<TransferFunds>(x => x.PaymentId == id);
        await probe.PublishAsync(new LedgerOperationRejected(command.OperationId, id, LedgerOperationKind.TransferFunds, "InsufficientFunds"), id);
        var failed = await probe.WaitForAsync<PaymentFailed>(x => x.PaymentId == id);
        failed.Status.ShouldBe("Failed");
        failed.ReasonCode.ShouldBe("InsufficientFunds");
    }

    [Fact]
    public async Task Duplicate_and_late_replies_do_not_move_a_final_payment()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var (_, id) = await StartTransferAsync(host, probe);
        var command = await probe.WaitForAsync<TransferFunds>(x => x.PaymentId == id);
        var reply = new LedgerOperationSucceeded(command.OperationId, id, LedgerOperationKind.TransferFunds, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await probe.PublishAsync(reply, id);
        await probe.WaitForAsync<PaymentCompleted>(x => x.PaymentId == id);
        await probe.PublishAsync(reply, id);
        var lateMessageId = Guid.NewGuid();
        await probe.PublishAsync(new LedgerOperationRejected(Guid.NewGuid(), id, LedgerOperationKind.TransferFunds, "Frozen"), id, lateMessageId);
        await Eventually.ShouldAsync(async () =>
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<InboxMessage>().AnyAsync(x => x.MessageId == lateMessageId);
        });
        await using var verification = host.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await db.Payments.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken)).Status.ToString().ShouldBe("Completed");
        (await db.SagaSteps.CountAsync(x => x.PaymentId == id, TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    private static async Task<(Guid Owner, Guid Id)> StartTransferAsync(ServiceHost<Program> host, BrokerProbe probe)
    {
        var owner = Guid.NewGuid();
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        using var client = host.CreateClient(owner);
        using var response = await PaymentTestSetup.TransferAsync(client, from, to);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (owner, (await PaymentTestSetup.BodyAsync(response)).GetProperty("id").GetGuid());
    }
}
