using Ledgerline.Contracts.Payments;

namespace Ledgerline.Payments.Sagas;

public static partial class PaymentSaga
{
    private static SagaDecision OnMerchantRequest(PaymentSnapshot payment, SagaInput input, DateTimeOffset now)
    {
        if (payment.Status != PaymentStatus.Authorized)
            return new(payment.Status, payment.Step, [], payment.FailureReason, false, InvalidState: true);
        if (input is SagaInput.VoidRequested) return BeginVoid(payment, now, "Requested");
        var amount = ((SagaInput.CaptureRequested)input).AmountMinor;
        return amount <= 0 || amount > payment.AmountMinor
            ? new(payment.Status, payment.Step, [], payment.FailureReason, false, InvalidState: true)
            : BeginCapture(payment, amount, now);
    }

    private static SagaDecision OnMerchantLedger(PaymentSnapshot payment, SagaInput input, DateTimeOffset now)
    {
        var expected = OperationIds.For(payment.Id, payment.Step);
        return (payment.Status, payment.Step, input) switch
        {
            (PaymentStatus.Processing, SagaStep.LedgerReserve, SagaInput.LedgerSucceeded reply) when reply.OperationId == expected =>
                payment.CaptureMode == CaptureMode.Automatic ? BeginCapture(payment, payment.AmountMinor, now) : Authorize(payment, now),
            (PaymentStatus.Processing, SagaStep.LedgerReserve, SagaInput.LedgerRejected reply) when reply.OperationId == expected =>
                Fail(payment, PaymentStatus.Failed, reply.Reason, now),
            (PaymentStatus.Capturing, SagaStep.LedgerCapture, SagaInput.LedgerSucceeded reply) when reply.OperationId == expected => Captured(payment, now),
            (PaymentStatus.Capturing, SagaStep.LedgerCapture, SagaInput.LedgerRejected reply) when reply.OperationId == expected => RestoreAuthorization(payment, reply.Reason, now),
            (PaymentStatus.Voiding, SagaStep.LedgerRelease, SagaInput.LedgerSucceeded reply) when reply.OperationId == expected => Voided(payment, now),
            (PaymentStatus.Voiding, SagaStep.LedgerRelease, SagaInput.LedgerRejected reply) when reply.OperationId == expected => Fail(payment, PaymentStatus.ManualReview, reply.Reason, now),
            _ => Ignore(payment)
        };
    }

    private static SagaDecision Authorize(PaymentSnapshot payment, DateTimeOffset now)
    {
        var expires = now.AddDays(7);
        return new(PaymentStatus.Authorized, SagaStep.None,
            [new SagaEffect.CancelTimeouts(), new SagaEffect.SetAuthorizationExpiresAt(expires),
                new SagaEffect.ScheduleTimeout(SagaStep.None, TimeSpan.FromDays(7)),
                new SagaEffect.PublishEvent(new PaymentAuthorized(payment.Id, payment.Kind.ToString(), payment.CustomerId,
                    payment.AmountMinor, payment.Currency, payment.SourceAccountId, payment.DestinationAccountId, now, expires))], null, false);
    }

    private static SagaDecision BeginCapture(PaymentSnapshot payment, long amount, DateTimeOffset now) =>
        AddEffects(Send(payment with { CaptureRequestedMinor = amount, FailureReason = null }, SagaStep.LedgerCapture,
            now, true, PaymentStatus.Capturing), new SagaEffect.SetCaptureRequestedMinor(amount));

    private static SagaDecision BeginVoid(PaymentSnapshot payment, DateTimeOffset now, string reason) =>
        Send(payment with { FailureReason = reason }, SagaStep.LedgerRelease, now, true, PaymentStatus.Voiding);

    private static SagaDecision Captured(PaymentSnapshot payment, DateTimeOffset now)
    {
        var amount = payment.CaptureRequestedMinor ?? payment.AmountMinor;
        return new(PaymentStatus.Captured, SagaStep.None,
            [new SagaEffect.CancelTimeouts(), new SagaEffect.SetCapturedMinor(amount), new SagaEffect.SetAuthorizationExpiresAt(null),
                new SagaEffect.PublishEvent(new PaymentCaptured(payment.Id, payment.Kind.ToString(), payment.CustomerId,
                    payment.AmountMinor, payment.Currency, payment.SourceAccountId, payment.DestinationAccountId, now, amount))], null, false);
    }

    private static SagaDecision RestoreAuthorization(PaymentSnapshot payment, string reason, DateTimeOffset now)
    {
        var remaining = (payment.AuthorizationExpiresAt ?? now.AddDays(7)) - now;
        return new(PaymentStatus.Authorized, SagaStep.None,
            [new SagaEffect.CancelTimeouts(), new SagaEffect.SetCaptureRequestedMinor(null),
                new SagaEffect.ScheduleTimeout(SagaStep.None, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)], reason, false);
    }

    private static SagaDecision Voided(PaymentSnapshot payment, DateTimeOffset now) => new(PaymentStatus.Voided, SagaStep.None,
        [new SagaEffect.CancelTimeouts(), new SagaEffect.SetAuthorizationExpiresAt(null),
            new SagaEffect.PublishEvent(new PaymentVoided(payment.Id, payment.Kind.ToString(), payment.CustomerId,
                payment.AmountMinor, payment.Currency, payment.SourceAccountId, payment.DestinationAccountId, now, payment.FailureReason ?? "Requested"))], null, false);
}
