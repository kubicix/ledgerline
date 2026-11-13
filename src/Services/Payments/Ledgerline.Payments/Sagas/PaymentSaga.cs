using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Contracts.Risk;

namespace Ledgerline.Payments.Sagas;

public static partial class PaymentSaga
{
    public static SagaDecision Start(PaymentSnapshot payment, DateTimeOffset now) => Start(payment, now, new());

    public static SagaDecision Start(PaymentSnapshot payment, DateTimeOffset now, PaymentSagaOptions options)
    {
        if (options.RiskEnabled) return Send(payment, SagaStep.RiskCheck, now, cancelTimeouts: false);
        var step = payment.Kind switch
        {
            PaymentKind.Transfer => SagaStep.LedgerTransfer,
            PaymentKind.TopUp => options.BankEnabled ? SagaStep.BankAuthorize : SagaStep.LedgerCredit,
            PaymentKind.MerchantPayment => SagaStep.LedgerReserve,
            _ => throw new NotSupportedException("Unsupported payment kind.")
        };
        return Send(payment, step, now, cancelTimeouts: false);
    }

    public static SagaDecision Decide(PaymentSnapshot payment, SagaInput input, DateTimeOffset now) => Decide(payment, input, now, new());

    public static SagaDecision Decide(PaymentSnapshot payment, SagaInput input, DateTimeOffset now, PaymentSagaOptions options)
    {
        if (payment.Kind == PaymentKind.MerchantPayment && input is SagaInput.CaptureRequested or SagaInput.VoidRequested)
            return OnMerchantRequest(payment, input, now);
        if (IsFinal(payment.Status)) return Ignore(payment);
        if (payment.Kind == PaymentKind.MerchantPayment && payment.Status == PaymentStatus.Authorized &&
            (input is SagaInput.AuthorizationExpired || input is SagaInput.StepTimedOut { Step: SagaStep.None }))
            return BeginVoid(payment, now, "Expired");
        if (input is SagaInput.StepTimedOut timeout && timeout.Step == payment.Step)
            return OnTimeout(payment, now, options);
        if (payment.Step == SagaStep.RiskCheck && payment.Status == PaymentStatus.Processing)
            return OnRisk(payment, input, now, options);
        if (payment.Kind == PaymentKind.TopUp && options.BankEnabled)
        {
            var bank = OnBank(payment, input, now);
            if (!bank.Ignored) return bank;
        }
        if (payment.Kind == PaymentKind.MerchantPayment) return OnMerchantLedger(payment, input, now);
        if (payment.Step is not (SagaStep.LedgerCredit or SagaStep.LedgerTransfer)) return Ignore(payment);
        if (payment.Status != PaymentStatus.Processing) return Ignore(payment);
        var operationId = OperationIds.For(payment.Id, payment.Step);
        return input switch
        {
            SagaInput.LedgerSucceeded reply when reply.OperationId == operationId => Complete(payment, now),
            SagaInput.LedgerRejected reply when reply.OperationId == operationId => options.BankEnabled && payment.Kind == PaymentKind.TopUp
                ? Send(payment with { FailureReason = reply.Reason }, SagaStep.BankRefund, now, true, PaymentStatus.Compensating)
                : Fail(payment, PaymentStatus.Failed, reply.Reason, now),
            _ => Ignore(payment)
        };
    }

    private static SagaDecision Send(PaymentSnapshot payment, SagaStep step, DateTimeOffset now, bool cancelTimeouts,
        PaymentStatus status = PaymentStatus.Processing, bool retry = false)
    {
        List<SagaEffect> effects = [];
        if (cancelTimeouts) effects.Add(new SagaEffect.CancelTimeouts());
        effects.Add(new SagaEffect.SendCommand(Command(payment, step, now)));
        var seconds = step switch
        {
            SagaStep.BankAuthorize or SagaStep.BankCapture => 15,
            SagaStep.BankVoid or SagaStep.BankRefund => retry ? CompensationDelay(payment.StepAttempt) : 2,
            _ => 30
        };
        effects.Add(new SagaEffect.ScheduleTimeout(step, TimeSpan.FromSeconds(seconds)));
        return new(status, step, effects, payment.FailureReason, false);
    }

    private static object Command(PaymentSnapshot payment, SagaStep step, DateTimeOffset now) => step switch
    {
        SagaStep.RiskCheck => new EvaluateRisk(payment.Id, payment.Kind.ToString(), payment.CustomerId,
            payment.Kind == PaymentKind.TopUp ? payment.DestinationAccountId : payment.SourceAccountId!.Value,
            payment.AmountMinor, payment.Currency, payment.CreatedAt ?? now),
        SagaStep.BankAuthorize => new CallBank(payment.Id, "Authorize"),
        SagaStep.BankCapture => new CallBank(payment.Id, "Capture"),
        SagaStep.BankVoid => new CallBank(payment.Id, "Void"),
        SagaStep.BankRefund => new CallBank(payment.Id, "Refund"),
        SagaStep.LedgerTransfer => new TransferFunds(OperationIds.For(payment.Id, step), payment.Id,
            payment.SourceAccountId!.Value, payment.DestinationAccountId, payment.AmountMinor, payment.Currency),
        SagaStep.LedgerCredit => new CreditTopUp(OperationIds.For(payment.Id, step), payment.Id,
            payment.DestinationAccountId, payment.AmountMinor, payment.Currency),
        SagaStep.LedgerReserve => new ReserveFunds(OperationIds.For(payment.Id, step), payment.Id,
            payment.SourceAccountId!.Value, payment.AmountMinor, payment.Currency),
        SagaStep.LedgerCapture => new CaptureFunds(OperationIds.For(payment.Id, step), payment.Id,
            payment.DestinationAccountId, payment.CaptureRequestedMinor ?? payment.AmountMinor),
        SagaStep.LedgerRelease => new ReleaseFunds(OperationIds.For(payment.Id, step), payment.Id),
        _ => throw new NotSupportedException("Unsupported saga command step.")
    };

    private static SagaDecision OnRisk(PaymentSnapshot payment, SagaInput input, DateTimeOffset now, PaymentSagaOptions options) => input switch
    {
        SagaInput.RiskRejected rejected => Fail(payment, PaymentStatus.Rejected, rejected.Reason, now),
        SagaInput.RiskApproved => Send(payment, payment.Kind switch
        {
            PaymentKind.Transfer => SagaStep.LedgerTransfer,
            PaymentKind.MerchantPayment => SagaStep.LedgerReserve,
            _ => options.BankEnabled ? SagaStep.BankAuthorize : SagaStep.LedgerCredit
        }, now, true),
        _ => Ignore(payment)
    };

    private static SagaDecision OnBank(PaymentSnapshot payment, SagaInput input, DateTimeOffset now) => (payment.Status, payment.Step, input) switch
    {
        (PaymentStatus.Processing, SagaStep.BankAuthorize, SagaInput.BankAuthorized authorized) =>
            AddEffects(Send(payment, SagaStep.BankCapture, now, true), new SagaEffect.SetBankAuthorizationId(authorized.Id), new SagaEffect.ClearCardToken()),
        (PaymentStatus.Processing, SagaStep.BankAuthorize, SagaInput.BankDeclined declined) =>
            Fail(payment, PaymentStatus.Failed, "BankDeclined:" + declined.Code, now),
        (PaymentStatus.Processing, SagaStep.BankCapture, SagaInput.BankCaptured captured) =>
            AddEffects(Send(payment, SagaStep.LedgerCredit, now, true), new SagaEffect.SetBankCaptureId(captured.Id)),
        (PaymentStatus.Processing, SagaStep.BankCapture, SagaInput.BankCaptureDeclined declined) =>
            Send(payment with { FailureReason = "BankDeclined:" + declined.Code }, SagaStep.BankVoid, now, true, PaymentStatus.Compensating),
        (PaymentStatus.Compensating, SagaStep.BankVoid or SagaStep.BankRefund, SagaInput.BankCompensated) =>
            Fail(payment, PaymentStatus.Compensated, payment.FailureReason ?? "BankCompensated", now),
        _ => Ignore(payment)
    };

    private static SagaDecision AddEffects(SagaDecision decision, params SagaEffect[] effects) =>
        decision with { Effects = effects.Concat(decision.Effects).ToArray() };

    private static int CompensationDelay(int attempt) => attempt >= 9 ? 300 : 1 << attempt;

    private static SagaDecision OnTimeout(PaymentSnapshot payment, DateTimeOffset now, PaymentSagaOptions options)
    {
        if (payment.Step == SagaStep.None) return Ignore(payment);
        var compensation = payment.Step is SagaStep.BankVoid or SagaStep.BankRefund;
        var limit = compensation ? options.BankCompensationMaxAttempts : 5;
        if (payment.StepAttempt < limit) return Send(payment, payment.Step, now, true, payment.Status, retry: true);
        var reason = payment.Step switch
        {
            SagaStep.RiskCheck => "RiskUnavailable",
            SagaStep.BankAuthorize or SagaStep.BankCapture or SagaStep.BankVoid or SagaStep.BankRefund => payment.FailureReason ?? "BankUnavailable",
            _ => "LedgerUnavailable"
        };
        return Fail(payment, payment.Step == SagaStep.RiskCheck ? PaymentStatus.Failed : PaymentStatus.ManualReview, reason, now);
    }

    private static SagaDecision Complete(PaymentSnapshot payment, DateTimeOffset now) => new(PaymentStatus.Completed, SagaStep.None,
        [new SagaEffect.CancelTimeouts(), new SagaEffect.PublishEvent(new PaymentCompleted(payment.Id, payment.Kind.ToString(),
            payment.CustomerId, payment.AmountMinor, payment.Currency, payment.SourceAccountId, payment.DestinationAccountId, now, payment.BankCaptureId))], null, false);

    private static SagaDecision Fail(PaymentSnapshot payment, PaymentStatus status, string reason, DateTimeOffset now) => new(status, SagaStep.None,
        [new SagaEffect.CancelTimeouts(), new SagaEffect.PublishEvent(new PaymentFailed(payment.Id, payment.Kind.ToString(),
            payment.CustomerId, payment.AmountMinor, payment.Currency, payment.SourceAccountId, payment.DestinationAccountId, now, status.ToString(), reason))], reason, false);

    private static SagaDecision Ignore(PaymentSnapshot payment) => new(payment.Status, payment.Step, [], payment.FailureReason, true);

    private static bool IsFinal(PaymentStatus status) => status is PaymentStatus.Completed or PaymentStatus.Voided or PaymentStatus.Compensated
        or PaymentStatus.Rejected or PaymentStatus.Failed or PaymentStatus.ManualReview or PaymentStatus.Captured;

}
