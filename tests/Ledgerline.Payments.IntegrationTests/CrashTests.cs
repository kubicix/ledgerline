using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Ledgerline.Payments.Sagas;
using Ledgerline.Testing;
using Ledgerline.Testing.Faults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledgerline.Payments.IntegrationTests;

public sealed class CrashTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Crash_after_saga_transition_commit_resumes_from_outbox()
    {
        var crashed = await ServiceHost<Program>.StartAsync(infrastructure, "saga_crash", services =>
        {
            services.AddSingleton<ICrashPoint>(provider => new StoppingCrashPoint("saga.after-transition-commit", provider.GetRequiredService<IHostApplicationLifetime>()));
            services.Remove(services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(OutboxRelay<PaymentsDbContext>)));
        });
        var cs = crashed.ConnectionString;
        await using var probe = await PaymentTestSetup.ProbeAsync(crashed);
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        var fault = (StoppingCrashPoint)crashed.Services.GetRequiredService<ICrashPoint>();
        try
        {
            var from = await PaymentTestSetup.AddAccountAsync(crashed, probe, owner);
            var to = await PaymentTestSetup.AddAccountAsync(crashed, probe, Guid.NewGuid());
            await using var scope = crashed.Services.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<SagaRunner>();
            await Should.ThrowAsync<HostAbortedException>(() => runner.StartAsync(new Payment
            {
                Id = id, Kind = PaymentKind.Transfer, CustomerId = owner, SourceAccountId = from,
                DestinationAccountId = to, AmountMinor = 2500, Currency = "TRY", IdempotencyKey = "crash",
                RequestHash = "synthetic", CreatedAt = DateTimeOffset.UtcNow
            }, TestContext.Current.CancellationToken));
            await fault.Crashed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally { await crashed.DisposeAsync(); }
        var inspectionConnection = new Npgsql.NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString;
        await using (var db = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(inspectionConnection).Options))
        {
            (await db.Payments.SingleAsync(TestContext.Current.CancellationToken)).Id.ShouldBe(id);
            (await db.Set<OutboxMessage>().SingleAsync(TestContext.Current.CancellationToken)).ProcessedAt.ShouldBeNull();
        }
        await using var restarted = await ServiceHost<Program>.StartOnDatabaseAsync(infrastructure, cs);
        var command = await probe.WaitForAsync<TransferFunds>(x => x.PaymentId == id);
        await probe.PublishAsync(new LedgerOperationSucceeded(command.OperationId, id, LedgerOperationKind.TransferFunds, Guid.NewGuid(), DateTimeOffset.UtcNow), id);
        await probe.WaitForAsync<PaymentCompleted>(x => x.PaymentId == id);
        await using var verification = restarted.Services.CreateAsyncScope();
        var verified = verification.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await verified.Payments.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken)).Status.ToString().ShouldBe("Completed");
        (await verified.SagaSteps.CountAsync(x => x.PaymentId == id, TestContext.Current.CancellationToken)).ShouldBe(2);
        probe.Messages.Count(x => x.Type == "accounts.command.transfer-funds.v1").ShouldBe(1);
    }
}
