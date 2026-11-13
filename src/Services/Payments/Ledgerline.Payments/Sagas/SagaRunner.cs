using System.Diagnostics.Metrics;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
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
    private Guid? lastCommandMessageId;

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

    public Task ApplyAsync(Guid paymentId, SagaInput input, MessageContext? context, CancellationToken ct) =>
        ApplyAndGetDecisionAsync(paymentId, input, context, ct);

    internal async Task<SagaDecision> ApplyAndGetDecisionAsync(Guid paymentId, SagaInput input, MessageContext? context, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var payment = await db.Payments.SingleOrDefaultAsync(x => x.Id == paymentId, ct)
            ?? throw new InvalidOperationException("The ledger reply references an unknown payment.");
        var snapshot = payment.Snapshot();
        if (payment.SagaStep == SagaStep.LedgerCapture)
            snapshot = snapshot with { CaptureOperationId = await SagaActionAudit.CaptureOperationAsync(db, paymentId, ct) };
        var decision = PaymentSaga.Decide(snapshot, input, clock.UtcNow, sagaOptions);
        if (decision.Ignored || decision.InvalidState) return decision;
        var oldStep = payment.SagaStep;
        if (decision.Step != oldStep && decision.Step != SagaStep.None) payment.StepAttempt = 1;
        var outcome = input switch
        {
            SagaInput.LedgerSucceeded or SagaInput.RiskApproved or SagaInput.BankAuthorized or SagaInput.BankCaptured or SagaInput.BankCompensated => "Succeeded",
            SagaInput.LedgerRejected or SagaInput.RiskRejected or SagaInput.BankDeclined or SagaInput.BankCaptureDeclined => "Rejected",
            SagaInput.CaptureRequested or SagaInput.VoidRequested => "Started",
            _ => "Retried"
        };
        if (input is SagaInput.StepTimedOut)
        {
            outcome = decision.Step == oldStep ? "Retried" : "TimedOut";
            if (decision.Step == oldStep) payment.StepAttempt++;
        }
        var auditedStep = input is SagaInput.CaptureRequested or SagaInput.VoidRequested ? decision.Step : oldStep;
        await ApplyDecisionAsync(payment, decision, outcome, auditedStep, context, ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
            OnCommitted();
        }
        return decision;
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
        lastCommandMessageId = null;
        foreach (var effect in decision.Effects) await ApplyEffectAsync(payment, effect, context, ct);
        await db.SaveChangesAsync(ct);
        transitioned = true;
        manualReview = decision.Status == PaymentStatus.ManualReview;
    }

    private async Task ApplyEffectAsync(Payment payment, SagaEffect effect, MessageContext? context, CancellationToken ct)
    {
        if (ApplyStateEffect(payment, effect)) return;
        switch (effect)
        {
            case SagaEffect.CancelTimeouts:
                db.SagaTimeouts.RemoveRange(await db.SagaTimeouts.Where(x => x.PaymentId == payment.Id).ToListAsync(ct));
                break;
            case SagaEffect.ScheduleTimeout timeout:
                var after = timeout.Step switch
                {
                    SagaStep.RiskCheck or SagaStep.LedgerCredit or SagaStep.LedgerTransfer or SagaStep.LedgerReserve or SagaStep.LedgerCapture or SagaStep.LedgerRelease =>
                        TimeSpan.FromMilliseconds(timeoutOptions.StepTimeoutMs),
                    SagaStep.BankVoid or SagaStep.BankRefund when timeoutOptions.CompensationTimeoutMs is { } milliseconds => TimeSpan.FromMilliseconds(milliseconds),
                    _ => timeout.After
                };
                db.SagaTimeouts.Add(new SagaTimeout { Id = Ids.New(), PaymentId = payment.Id, Step = timeout.Step,
                    DueAt = clock.UtcNow + after, DurationMs = checked((int)after.TotalMilliseconds), CommandMessageId = lastCommandMessageId });
                break;
            default:
                PublishEffect(payment.Id, effect, context?.MessageId);
                break;
        }
    }

    private static bool ApplyStateEffect(Payment payment, SagaEffect effect)
    {
        switch (effect)
        {
            case SagaEffect.SetBankAuthorizationId authorization:
                payment.BankAuthorizationId = authorization.Id;
                return true;
            case SagaEffect.SetBankCaptureId capture:
                payment.BankCaptureId = capture.Id;
                return true;
            case SagaEffect.ClearCardToken:
                payment.CardToken = null;
                return true;
            case SagaEffect.SetAuthorizationExpiresAt expiry:
                payment.AuthorizationExpiresAt = expiry.At;
                return true;
            case SagaEffect.SetCaptureRequestedMinor requested:
                payment.CaptureRequestedMinor = requested.AmountMinor;
                return true;
            case SagaEffect.SetCapturedMinor captured:
                payment.CapturedMinor = captured.AmountMinor;
                return true;
            default: return false;
        }
    }

    private void PublishEffect(Guid paymentId, SagaEffect effect, Guid? causationId)
    {
        switch (effect)
        {
            case SagaEffect.SendCommand send: EnqueueCommand(paymentId, send.Message, causationId); break;
            case SagaEffect.PublishEvent publish: EnqueueEvent(paymentId, publish.Message, causationId); break;
            default: throw new NotSupportedException("The saga emitted an unsupported effect.");
        }
    }

    private void EnqueueCommand(Guid paymentId, object message, Guid? causationId)
    {
        var existing = db.ChangeTracker.Entries<OutboxMessage>().Select(x => x.Entity.MessageId).ToHashSet();
        switch (message)
        {
            case CreditTopUp command:
                outbox.Enqueue(command, paymentId, causationId);
                break;
            case TransferFunds command:
                outbox.Enqueue(command, paymentId, causationId);
                break;
            case CallBank command:
                outbox.Enqueue(command, paymentId, causationId);
                break;
            case EvaluateRisk command:
                outbox.Enqueue(command, paymentId, causationId);
                break;
            case ReserveFunds command:
                outbox.Enqueue(command, paymentId, causationId);
                break;
            case CaptureFunds command:
                outbox.Enqueue(command, paymentId, causationId);
                break;
            case ReleaseFunds command:
                outbox.Enqueue(command, paymentId, causationId);
                break;
            default: throw new NotSupportedException("The saga emitted an unsupported command.");
        }
        lastCommandMessageId = db.ChangeTracker.Entries<OutboxMessage>().Single(x => !existing.Contains(x.Entity.MessageId)).Entity.MessageId;
    }

    private void EnqueueEvent(Guid paymentId, object value, Guid? causationId)
    {
        switch (value)
        {
            case PaymentCompleted message:
                outbox.Enqueue(message, paymentId, causationId);
                break;
            case PaymentFailed message:
                outbox.Enqueue(message, paymentId, causationId);
                break;
            case PaymentAuthorized message:
                outbox.Enqueue(message, paymentId, causationId);
                break;
            case PaymentCaptured message:
                outbox.Enqueue(message, paymentId, causationId);
                break;
            case PaymentVoided message:
                outbox.Enqueue(message, paymentId, causationId);
                break;
            default: throw new NotSupportedException("The saga emitted an unsupported event.");
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
