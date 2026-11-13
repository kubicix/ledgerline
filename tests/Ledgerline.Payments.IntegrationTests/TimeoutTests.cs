using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Contracts.Risk;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Sagas;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Payments.IntegrationTests;

[Collection("Redis lifecycle")]
public sealed class TimeoutTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Retained_command_cleanup_does_not_stall_this_or_other_due_payments()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "retained_timeout",
            services => services.AddSingleton(new TimeoutOptions { StepTimeoutMs = 60000, PollIntervalMs = 20 }));
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var first = await StartTransferAsync(host, probe);
        var second = await StartTransferAsync(host, probe);
        await probe.WaitForAsync<TransferFunds>(x => x.PaymentId == first);
        await probe.WaitForAsync<TransferFunds>(x => x.PaymentId == second);
        await Eventually.ShouldAsync(async () =>
        {
            await using var scope = host.Services.CreateAsyncScope();
            return !await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<OutboxMessage>().AnyAsync(x => x.ProcessedAt == null);
        });
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            await db.Set<OutboxMessage>().Where(x => x.CorrelationId == first && x.Type == "accounts.command.transfer-funds.v1" && x.ProcessedAt != null)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            await db.SagaTimeouts.Where(x => x.PaymentId == first).ExecuteUpdateAsync(set =>
                set.SetProperty(x => x.DueAt, DateTimeOffset.UtcNow.AddSeconds(-2)).SetProperty(x => x.DurationMs, 0), TestContext.Current.CancellationToken);
            await db.SagaTimeouts.Where(x => x.PaymentId == second).ExecuteUpdateAsync(set =>
                set.SetProperty(x => x.DueAt, DateTimeOffset.UtcNow.AddSeconds(-1)).SetProperty(x => x.DurationMs, 0), TestContext.Current.CancellationToken);
        }
        await Eventually.ShouldAsync(() => Task.FromResult(Commands(probe, first).Length >= 2 && Commands(probe, second).Length >= 2));
    }

    [Fact]
    public Task Step_attempts_are_not_consumed_while_broker_is_down() => BrokerOutageAsync(1);

    [Fact]
    public Task Topups_accepted_during_broker_outage_complete_after_recovery() => BrokerOutageAsync(3);

    private async Task BrokerOutageAsync(int count)
    {
        await using var host = await StartAsync();
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var account = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        using var client = host.CreateClient(owner);
        await infrastructure.StopRabbitMqAsync();
        var ids = new List<Guid>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/topups")
                { Content = System.Net.Http.Json.JsonContent.Create(new { accountId = account, amountMinor = 100, currency = "TRY" }) };
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
                using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
                response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
                ids.Add((await PaymentTestSetup.BodyAsync(response)).GetProperty("id").GetGuid());
            }
            await Task.Delay(3000, TestContext.Current.CancellationToken);
            await using var scope = host.Services.CreateAsyncScope();
            var rows = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Payments.ToArrayAsync(TestContext.Current.CancellationToken);
            rows.Length.ShouldBe(count);
            rows.ShouldAllBe(row => row.Status == PaymentStatus.Processing && row.StepAttempt == 1);
        }
        finally { await infrastructure.StartRabbitMqAsync(); }
        foreach (var id in ids)
        {
            var command = await probe.WaitForAsync<CreditTopUp>(x => x.PaymentId == id, TimeSpan.FromSeconds(30));
            await probe.PublishAsync(new LedgerOperationSucceeded(command.OperationId, id, LedgerOperationKind.CreditTopUp, Guid.NewGuid(), DateTimeOffset.UtcNow), id);
            await probe.WaitForAsync<PaymentCompleted>(x => x.PaymentId == id);
        }
        await using var verify = host.Services.CreateAsyncScope();
        (await verify.ServiceProvider.GetRequiredService<PaymentsDbContext>().Payments.ToArrayAsync(TestContext.Current.CancellationToken))
            .ShouldAllBe(row => row.Status == PaymentStatus.Completed && row.StepAttempt <= 2);
    }

    [Fact]
    public async Task Risk_unavailable_fails_payment_after_5_attempts_with_RiskUnavailable()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "risk_timeout", services =>
        {
            services.AddSingleton(new PaymentSagaOptions { RiskEnabled = true, BankEnabled = false });
            services.AddSingleton(new TimeoutOptions { StepTimeoutMs = 100, PollIntervalMs = 20 });
        });
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<Ledgerline.BuildingBlocks.Messaging.RabbitMq.RabbitMqOptions>().Uri,
            "accounts.command.#", "payments.event.#", "risk.command.#");
        var id = await StartTransferAsync(host, probe);
        var failed = await probe.WaitForAsync<PaymentFailed>(x => x.PaymentId == id, TimeSpan.FromSeconds(8));
        failed.Status.ShouldBe("Failed");
        failed.ReasonCode.ShouldBe("RiskUnavailable");
        probe.Messages.Count(x => x.Type == "risk.command.evaluate-risk.v1" && x.CorrelationId == id).ShouldBe(5);
    }

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
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.DueAt, DateTimeOffset.UtcNow.AddSeconds(-1))
                    .SetProperty(x => x.DurationMs, 0), TestContext.Current.CancellationToken);
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
