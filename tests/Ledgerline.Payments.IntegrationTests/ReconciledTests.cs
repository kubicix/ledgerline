using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Reconciliation;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Ledgerline.Payments.Sagas;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Payments.IntegrationTests;

/// <summary>The <c>payments-reconciled</c> event stamps <c>reconciled_at</c> once (payments.md section 5).</summary>
public sealed class ReconciledTests(InfrastructureFixture infrastructure)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PaymentsReconciled_sets_reconciled_at_once()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri);
        var payment = await AddPaymentAsync(host);
        var run = Guid.NewGuid();
        var firstMessage = Guid.NewGuid();
        await probe.PublishAsync(new PaymentsReconciled(run, new DateOnly(2026, 11, 13), [payment]), run, firstMessage);
        await WaitForInboxAsync(host, firstMessage);
        var stamped = (await ReconciledAtAsync(host, payment)).ShouldNotBeNull();
        stamped.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow);

        // The second event is another message (a re-run publishes a new event set) and carries the same payment.
        var secondMessage = Guid.NewGuid();
        await probe.PublishAsync(new PaymentsReconciled(Guid.NewGuid(), new DateOnly(2026, 11, 14), [payment]), run, secondMessage);
        await WaitForInboxAsync(host, secondMessage);

        (await ReconciledAtAsync(host, payment)).ShouldBe(stamped);
    }

    [Fact]
    public async Task PaymentsReconciled_stamps_the_known_payments_of_a_batch_and_ignores_unknown_ids()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri);
        var first = await AddPaymentAsync(host);
        var second = await AddPaymentAsync(host);
        var untouched = await AddPaymentAsync(host);
        var message = Guid.NewGuid();

        await probe.PublishAsync(new PaymentsReconciled(Guid.NewGuid(), new DateOnly(2026, 11, 13), [first, Guid.NewGuid(), second]), Guid.NewGuid(), message);
        await WaitForInboxAsync(host, message);

        (await ReconciledAtAsync(host, first)).ShouldNotBeNull();
        (await ReconciledAtAsync(host, second)).ShouldNotBeNull();
        (await ReconciledAtAsync(host, untouched)).ShouldBeNull();
    }

    [Fact]
    public async Task PaymentsReconciled_with_no_payments_is_consumed_without_effect()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri);
        var payment = await AddPaymentAsync(host);
        var empty = Guid.NewGuid();
        await probe.PublishAsync(new PaymentsReconciled(Guid.NewGuid(), new DateOnly(2026, 11, 13), []), Guid.NewGuid(), empty);
        await WaitForInboxAsync(host, empty);
        var later = Guid.NewGuid();

        await probe.PublishAsync(new PaymentsReconciled(Guid.NewGuid(), new DateOnly(2026, 11, 13), [payment]), Guid.NewGuid(), later);
        await WaitForInboxAsync(host, later);

        (await ReconciledAtAsync(host, payment)).ShouldNotBeNull();
    }

    private static async Task<Guid> AddPaymentAsync(ServiceHost<Program> host)
    {
        var id = Guid.NewGuid();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        db.Payments.Add(new Payment
        {
            Id = id, Kind = PaymentKind.TopUp, Status = PaymentStatus.Completed, SagaStep = SagaStep.None, CustomerId = Guid.NewGuid(),
            DestinationAccountId = Guid.NewGuid(), AmountMinor = 2500, CapturedMinor = 2500, Currency = "TRY",
            IdempotencyKey = id.ToString("N"), RequestHash = "synthetic", CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private static async Task<DateTimeOffset?> ReconciledAtAsync(ServiceHost<Program> host, Guid payment)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.Payments.AsNoTracking().Where(x => x.Id == payment).Select(x => x.ReconciledAt).SingleAsync(Ct);
    }

    /// <summary>The inbox row is written in the handler's transaction, so its presence means the message was handled and committed.</summary>
    private static Task WaitForInboxAsync(ServiceHost<Program> host, Guid message) => Eventually.ShouldAsync(async () =>
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<InboxMessage>().AnyAsync(x => x.MessageId == message, Ct);
    });
}
