using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;

namespace Ledgerline.Payments.Sagas;

public static class PaymentSaga
{
    public static SagaDecision Start(PaymentSnapshot payment, DateTimeOffset now) => Start(payment, now, new());

    public static SagaDecision Start(PaymentSnapshot payment, DateTimeOffset now, PaymentSagaOptions options)
    {
        RequireM1(options);
        var step = payment.Kind switch
        {
            PaymentKind.Transfer => SagaStep.LedgerTransfer,
            PaymentKind.TopUp => SagaStep.LedgerCredit,
            _ => throw new NotSupportedException("Merchant payment steps are introduced in Milestone 2.")
        };
        return Send(payment, step, cancelTimeouts: false);
    }

    public static SagaDecision Decide(PaymentSnapshot payment, SagaInput input, DateTimeOffset now) => Decide(payment, input, now, new());

    public static SagaDecision Decide(PaymentSnapshot payment, SagaInput input, DateTimeOffset now, PaymentSagaOptions options)
    {
        if (IsFinal(payment.Status)) return Ignore(payment);
        RequireM1(options);
        if (payment.Step is not (SagaStep.LedgerCredit or SagaStep.LedgerTransfer)) return Ignore(payment);
        var operationId = OperationIds.For(payment.Id, payment.Step);
        return input switch
        {
            SagaInput.LedgerSucceeded reply when reply.OperationId == operationId => Complete(payment, now),
            SagaInput.LedgerRejected reply when reply.OperationId == operationId => Fail(payment, PaymentStatus.Failed, reply.Reason, now),
            SagaInput.StepTimedOut timeout when timeout.Step == payment.Step => payment.StepAttempt >= 5
                ? Fail(payment, PaymentStatus.ManualReview, "LedgerUnavailable", now)
                : Send(payment, payment.Step, cancelTimeouts: true),
            _ => Ignore(payment)
        };
    }

    private static SagaDecision Send(PaymentSnapshot payment, SagaStep step, bool cancelTimeouts)
    {
        var operationId = OperationIds.For(payment.Id, step);
        object command = step == SagaStep.LedgerTransfer
            ? new TransferFunds(operationId, payment.Id, payment.SourceAccountId!.Value, payment.DestinationAccountId, payment.AmountMinor, payment.Currency)
            : new CreditTopUp(operationId, payment.Id, payment.DestinationAccountId, payment.AmountMinor, payment.Currency);
        List<SagaEffect> effects = [];
        if (cancelTimeouts) effects.Add(new SagaEffect.CancelTimeouts());
        effects.Add(new SagaEffect.SendCommand(command));
        effects.Add(new SagaEffect.ScheduleTimeout(step, TimeSpan.FromSeconds(30)));
        return new(PaymentStatus.Processing, step, effects, null, false);
    }

    private static SagaDecision Complete(PaymentSnapshot payment, DateTimeOffset now) => new(PaymentStatus.Completed, SagaStep.None,
        [new SagaEffect.CancelTimeouts(), new SagaEffect.PublishEvent(new PaymentCompleted(payment.Id, payment.Kind.ToString(),
            payment.CustomerId, payment.AmountMinor, payment.Currency, payment.SourceAccountId, payment.DestinationAccountId, now, null))], null, false);

    private static SagaDecision Fail(PaymentSnapshot payment, PaymentStatus status, string reason, DateTimeOffset now) => new(status, SagaStep.None,
        [new SagaEffect.CancelTimeouts(), new SagaEffect.PublishEvent(new PaymentFailed(payment.Id, payment.Kind.ToString(),
            payment.CustomerId, payment.AmountMinor, payment.Currency, payment.SourceAccountId, payment.DestinationAccountId, now, status.ToString(), reason))], reason, false);

    private static SagaDecision Ignore(PaymentSnapshot payment) => new(payment.Status, payment.Step, [], payment.FailureReason, true);

    private static bool IsFinal(PaymentStatus status) => status is PaymentStatus.Completed or PaymentStatus.Voided or PaymentStatus.Compensated
        or PaymentStatus.Rejected or PaymentStatus.Failed or PaymentStatus.ManualReview or PaymentStatus.Captured;

    private static void RequireM1(PaymentSagaOptions options)
    {
        if (options.RiskEnabled || options.BankEnabled)
            throw new NotSupportedException("Risk and bank steps are introduced in Milestone 2.");
    }
}
