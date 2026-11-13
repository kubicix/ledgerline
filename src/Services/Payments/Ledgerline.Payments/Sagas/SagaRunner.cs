using System.Diagnostics.Metrics;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Contracts.Risk;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Sagas;

public sealed class SagaRunner(PaymentsDbContext db, IOutbox outbox, IClock clock, ICrashPoint crashPoint,
    TimeoutOptions timeoutOptions, PaymentSagaOptions sagaOptions) : IPostCommitHook
{
    private static readonly Meter Meter = new("Ledgerline.Payments");
    private static readonly Counter<long> ManualReviews = Meter.CreateCounter<long>("ledgerline.saga.manual_review");
    private bool transitioned;
    private bool manualReview;

    public async Task StartAsync(Payment payment, CancellationToken ct = default)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        db.Payments.Add(payment);
        var decision = PaymentSaga.Start(payment.Snapshot(), clock.UtcNow, sagaOptions);
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
        var decision = PaymentSaga.Decide(payment.Snapshot(), input, clock.UtcNow, sagaOptions);
        if (decision.Ignored) return;
        var oldStep = payment.SagaStep;
        if (decision.Step != oldStep && decision.Step != SagaStep.None) payment.StepAttempt = 1;
        var outcome = input switch { SagaInput.LedgerSucceeded => "Succeeded", SagaInput.LedgerRejected => "Rejected", _ => "Retried" };
        if (input is SagaInput.StepTimedOut)
        {
            outcome = decision.Step == oldStep ? "Retried" : "TimedOut";
            if (decision.Step == oldStep) payment.StepAttempt++;
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
            case SagaEffect.SetBankAuthorizationId authorization:
                payment.BankAuthorizationId = authorization.Id;
                break;
            case SagaEffect.SetBankCaptureId capture:
                payment.BankCaptureId = capture.Id;
                break;
            case SagaEffect.ClearCardToken:
                payment.CardToken = null;
                break;
            case SagaEffect.CancelTimeouts:
                db.SagaTimeouts.RemoveRange(await db.SagaTimeouts.Where(x => x.PaymentId == payment.Id).ToListAsync(ct));
                break;
            case SagaEffect.ScheduleTimeout timeout:
                var after = timeout.Step is SagaStep.LedgerCredit or SagaStep.LedgerTransfer
                    ? TimeSpan.FromMilliseconds(timeoutOptions.StepTimeoutMs) : timeout.After;
                db.SagaTimeouts.Add(new SagaTimeout { Id = Ids.New(), PaymentId = payment.Id, Step = timeout.Step, DueAt = clock.UtcNow + after });
                break;
            default:
                PublishEffect(payment.Id, effect, context?.MessageId);
                break;
        }
    }

    private void PublishEffect(Guid paymentId, SagaEffect effect, Guid? causationId)
    {
        switch (effect)
        {
            case SagaEffect.SendCommand { Message: CreditTopUp command }:
                outbox.Enqueue(command, paymentId, causationId);
                break;
            case SagaEffect.SendCommand { Message: TransferFunds command }:
                outbox.Enqueue(command, paymentId, causationId);
                break;
            case SagaEffect.SendCommand { Message: CallBank command }:
                outbox.Enqueue(command, paymentId, causationId);
                break;
            case SagaEffect.SendCommand { Message: EvaluateRisk command }:
                outbox.Enqueue(command, paymentId, causationId);
                break;
            case SagaEffect.PublishEvent { Message: PaymentCompleted message }:
                outbox.Enqueue(message, paymentId, causationId);
                break;
            case SagaEffect.PublishEvent { Message: PaymentFailed message }:
                outbox.Enqueue(message, paymentId, causationId);
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
