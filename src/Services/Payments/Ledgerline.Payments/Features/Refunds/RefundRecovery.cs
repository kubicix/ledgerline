using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Ledgerline.Payments.Sagas;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.Refunds;

public sealed class RefundRecovery(PaymentsDbContext db, IOutbox outbox, IClock clock, TimeoutOptions options)
{
    internal void Start(Refund refund, Payment payment) => Schedule(refund, payment, options.StepTimeoutMs);

    internal async Task RetryAsync(Guid refundId, Payment payment, int previousDelayMs, CancellationToken ct)
    {
        var refund = await db.Refunds.SingleAsync(x => x.Id == refundId, ct);
        if (refund.Status != RefundStatus.Processing) return;
        RefundAudit.Append(db, refund, "RefundRetried", clock.UtcNow);
        Schedule(refund, payment, Math.Min(checked(previousDelayMs * 2), 300000));
    }

    private void Schedule(Refund refund, Payment payment, int durationMs)
    {
        var decision = RefundSaga.Start(new(refund.Id, payment.Id, refund.AmountMinor, refund.Status), payment.Snapshot());
        var command = (RefundFunds)((SagaEffect.SendCommand)decision.Effects.Single()).Message;
        var existing = db.ChangeTracker.Entries<OutboxMessage>().Select(x => x.Entity.MessageId).ToHashSet();
        outbox.Enqueue(command, payment.Id);
        var messageId = db.ChangeTracker.Entries<OutboxMessage>().Single(x => !existing.Contains(x.Entity.MessageId)).Entity.MessageId;
        db.SagaTimeouts.Add(new SagaTimeout { Id = Ids.New(), PaymentId = payment.Id, RefundId = refund.Id,
            Step = SagaStep.None, DurationMs = durationMs, DueAt = clock.UtcNow.AddMilliseconds(durationMs), CommandMessageId = messageId });
    }
}
