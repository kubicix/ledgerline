using System.Diagnostics.Metrics;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Sagas;

public sealed class SagaRunner(PaymentsDbContext db, IOutbox outbox, IClock clock, ICrashPoint crashPoint) : IPostCommitHook
{
    private static readonly Meter Meter = new("Ledgerline.Payments");
    private static readonly Counter<long> ManualReviews = Meter.CreateCounter<long>("ledgerline.saga.manual_review");
    private bool transitioned;
    private bool manualReview;

    public async Task StartAsync(Payment payment, CancellationToken ct = default)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        db.Payments.Add(payment);
        var decision = PaymentSaga.Start(payment.Snapshot(), clock.UtcNow);
        await ApplyDecisionAsync(payment, decision, "Started", null, null, ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
            OnCommitted();
        }
    }

    public async Task ApplyAsync(Guid paymentId, SagaInput input, MessageContext? context, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var payment = await db.Payments.SingleOrDefaultAsync(x => x.Id == paymentId, ct)
            ?? throw new InvalidOperationException("The ledger reply references an unknown payment.");
        var decision = PaymentSaga.Decide(payment.Snapshot(), input, clock.UtcNow);
        if (decision.Ignored) return;
        var oldStep = payment.SagaStep;
        var outcome = input switch { SagaInput.LedgerSucceeded => "Succeeded", SagaInput.LedgerRejected => "Rejected", _ => "Retried" };
        if (input is SagaInput.StepTimedOut)
        {
            outcome = decision.Status == PaymentStatus.ManualReview ? "TimedOut" : "Retried";
            if (decision.Status != PaymentStatus.ManualReview) payment.StepAttempt++;
        }
        await ApplyDecisionAsync(payment, decision, outcome, oldStep, context, ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
            OnCommitted();
        }
    }

    private async Task ApplyDecisionAsync(Payment payment, SagaDecision decision, string outcome,
        SagaStep? auditedStep, MessageContext? context, CancellationToken ct)
    {
        payment.Status = decision.Status;
        payment.SagaStep = decision.Step;
        payment.FailureReason = decision.FailureReason;
        payment.UpdatedAt = clock.UtcNow;
        db.SagaSteps.Add(new SagaStepRow { PaymentId = payment.Id, Step = auditedStep ?? decision.Step, Outcome = outcome,
            Detail = decision.FailureReason, At = clock.UtcNow });
        foreach (var effect in decision.Effects) await ApplyEffectAsync(payment, effect, context, ct);
        await db.SaveChangesAsync(ct);
        transitioned = true;
        manualReview = decision.Status == PaymentStatus.ManualReview;
    }

    private async Task ApplyEffectAsync(Payment payment, SagaEffect effect, MessageContext? context, CancellationToken ct)
    {
        switch (effect)
        {
            case SagaEffect.CancelTimeouts:
                db.SagaTimeouts.RemoveRange(await db.SagaTimeouts.Where(x => x.PaymentId == payment.Id).ToListAsync(ct));
                break;
            case SagaEffect.ScheduleTimeout timeout:
                db.SagaTimeouts.Add(new SagaTimeout { Id = Ids.New(), PaymentId = payment.Id, Step = timeout.Step, DueAt = clock.UtcNow + timeout.After });
                break;
            case SagaEffect.SendCommand { Message: CreditTopUp command }:
                outbox.Enqueue(command, payment.Id, context?.MessageId);
                break;
            case SagaEffect.SendCommand { Message: TransferFunds command }:
                outbox.Enqueue(command, payment.Id, context?.MessageId);
                break;
            case SagaEffect.PublishEvent { Message: PaymentCompleted message }:
                outbox.Enqueue(message, payment.Id, context?.MessageId);
                break;
            case SagaEffect.PublishEvent { Message: PaymentFailed message }:
                outbox.Enqueue(message, payment.Id, context?.MessageId);
                break;
            default: throw new NotSupportedException("The saga emitted an unsupported effect.");
        }
    }

    public void OnCommitted()
    {
        if (!transitioned) return;
        transitioned = false;
        if (manualReview) ManualReviews.Add(1);
        crashPoint.Hit("saga.after-transition-commit");
    }
}
