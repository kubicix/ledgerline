using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Contracts.Risk;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Tests;

public sealed class TopUpSagaTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly PaymentSagaOptions Options = new() { RiskEnabled = true, BankEnabled = true };
    private static PaymentSnapshot Payment(SagaStep step = SagaStep.None) => new(Guid.NewGuid(), PaymentKind.TopUp,
        PaymentStatus.Processing, step, 1, Guid.NewGuid(), null, Guid.NewGuid(), 2500, "TRY");
    private static SagaDecision Decide(PaymentSnapshot payment, SagaInput input) => PaymentSaga.Decide(payment, input, Now, Options);
    private static T Command<T>(SagaDecision decision) => decision.Effects.OfType<SagaEffect.SendCommand>().Single().Message.ShouldBeOfType<T>();

    [Fact]
    public void T01_start_sends_EvaluateRisk()
    {
        var payment = Payment();
        var decision = PaymentSaga.Start(payment, Now, Options);
        decision.Step.ShouldBe(SagaStep.RiskCheck);
        Command<EvaluateRisk>(decision).ShouldBe(new(payment.Id, "TopUp", payment.CustomerId, payment.DestinationAccountId, 2500, "TRY", Now));
        decision.Effects.OfType<SagaEffect.ScheduleTimeout>().Single().After.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void T02_risk_approved_sends_BankAuthorize()
    {
        var payment = Payment(SagaStep.RiskCheck);
        var decision = Decide(payment, new SagaInput.RiskApproved());
        decision.Step.ShouldBe(SagaStep.BankAuthorize);
        Command<CallBank>(decision).ShouldBe(new(payment.Id, "Authorize"));
        decision.Effects.OfType<SagaEffect.ScheduleTimeout>().Single().After.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void T03_risk_rejected_ends_Rejected()
    {
        var decision = Decide(Payment(SagaStep.RiskCheck), new SagaInput.RiskRejected("DailyAmountLimit"));
        decision.Status.ShouldBe(PaymentStatus.Rejected);
        decision.Effects.OfType<SagaEffect.PublishEvent>().Single().Message.ShouldBeOfType<PaymentFailed>().ReasonCode.ShouldBe("DailyAmountLimit");
    }

    [Fact]
    public void T04_authorized_stores_authorization_and_sends_BankCapture()
    {
        var decision = Decide(Payment(SagaStep.BankAuthorize), new SagaInput.BankAuthorized("auth_1"));
        decision.Step.ShouldBe(SagaStep.BankCapture);
        decision.Effects.OfType<SagaEffect.SetBankAuthorizationId>().Single().Id.ShouldBe("auth_1");
        Command<CallBank>(decision).Operation.ShouldBe("Capture");
    }

    [Fact]
    public void T05_authorization_declined_ends_Failed()
    {
        var decision = Decide(Payment(SagaStep.BankAuthorize), new SagaInput.BankDeclined("do_not_honor"));
        decision.Status.ShouldBe(PaymentStatus.Failed);
        decision.FailureReason.ShouldBe("BankDeclined:do_not_honor");
    }

    [Fact]
    public void T06_captured_stores_capture_and_sends_LedgerCredit()
    {
        var payment = Payment(SagaStep.BankCapture);
        var decision = Decide(payment, new SagaInput.BankCaptured("cap_1"));
        decision.Step.ShouldBe(SagaStep.LedgerCredit);
        decision.Effects.OfType<SagaEffect.SetBankCaptureId>().Single().Id.ShouldBe("cap_1");
        Command<CreditTopUp>(decision).OperationId.ShouldBe(OperationIds.For(payment.Id, SagaStep.LedgerCredit));
    }

    [Fact]
    public void T07_capture_declined_starts_BankVoid_compensation()
    {
        var decision = Decide(Payment(SagaStep.BankCapture), new SagaInput.BankCaptureDeclined("capture_declined"));
        decision.Status.ShouldBe(PaymentStatus.Compensating);
        decision.Step.ShouldBe(SagaStep.BankVoid);
        Command<CallBank>(decision).Operation.ShouldBe("Void");
    }

    [Fact]
    public void T08_ledger_succeeded_ends_Completed()
    {
        var payment = Payment(SagaStep.LedgerCredit) with { BankCaptureId = "cap_1" };
        var decision = Decide(payment, new SagaInput.LedgerSucceeded(OperationIds.For(payment.Id, payment.Step)));
        decision.Status.ShouldBe(PaymentStatus.Completed);
        decision.Effects.OfType<SagaEffect.PublishEvent>().Single().Message.ShouldBeOfType<PaymentCompleted>().BankCaptureId.ShouldBe("cap_1");
    }

    [Fact]
    public void T09_ledger_rejected_starts_BankRefund_compensation()
    {
        var payment = Payment(SagaStep.LedgerCredit);
        var decision = Decide(payment, new SagaInput.LedgerRejected(OperationIds.For(payment.Id, payment.Step), "AccountFrozen"));
        decision.Status.ShouldBe(PaymentStatus.Compensating);
        decision.Step.ShouldBe(SagaStep.BankRefund);
        decision.FailureReason.ShouldBe("AccountFrozen");
        Command<CallBank>(decision).Operation.ShouldBe("Refund");
    }

    [Theory]
    [InlineData(SagaStep.BankVoid)]
    [InlineData(SagaStep.BankRefund)]
    public void T10_compensation_done_ends_Compensated_and_publishes_PaymentFailed(SagaStep step)
    {
        var payment = Payment(step) with { Status = PaymentStatus.Compensating, FailureReason = "OriginalFailure" };
        var decision = Decide(payment, new SagaInput.BankCompensated());
        decision.Status.ShouldBe(PaymentStatus.Compensated);
        var failed = decision.Effects.OfType<SagaEffect.PublishEvent>().Single().Message.ShouldBeOfType<PaymentFailed>();
        failed.Status.ShouldBe("Compensated");
        failed.ReasonCode.ShouldBe("OriginalFailure");
    }

    [Fact]
    public void Risk_timeout_after_max_attempts_fails_with_RiskUnavailable()
    {
        var payment = Payment(SagaStep.RiskCheck) with { StepAttempt = 5 };
        var decision = Decide(payment, new SagaInput.StepTimedOut(payment.Step));
        decision.Status.ShouldBe(PaymentStatus.Failed);
        decision.FailureReason.ShouldBe("RiskUnavailable");
    }

    [Theory]
    [InlineData(1, 2)] [InlineData(2, 4)] [InlineData(3, 8)] [InlineData(4, 16)]
    [InlineData(5, 32)] [InlineData(6, 64)] [InlineData(7, 128)] [InlineData(8, 256)] [InlineData(9, 300)]
    public void Bank_compensation_timeout_uses_exponential_backoff_capped_at_300s(int attempt, int seconds)
    {
        var payment = Payment(SagaStep.BankRefund) with { Status = PaymentStatus.Compensating, StepAttempt = attempt };
        var decision = PaymentSaga.Decide(payment, new SagaInput.StepTimedOut(payment.Step), Now,
            new() { RiskEnabled = true, BankEnabled = true, BankCompensationMaxAttempts = 10 });
        decision.Effects.OfType<SagaEffect.ScheduleTimeout>().Single().After.ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void Bank_compensation_after_8_attempts_ends_ManualReview()
    {
        var payment = Payment(SagaStep.BankRefund) with { Status = PaymentStatus.Compensating, StepAttempt = 8 };
        Decide(payment, new SagaInput.StepTimedOut(payment.Step)).Status.ShouldBe(PaymentStatus.ManualReview);
    }

    [Fact]
    public void Card_token_is_cleared_after_authorization() =>
        Decide(Payment(SagaStep.BankAuthorize), new SagaInput.BankAuthorized("auth_1")).Effects.OfType<SagaEffect.ClearCardToken>().Count().ShouldBe(1);

    [Fact]
    public void M1_behavior_unchanged_when_risk_and_bank_disabled()
    {
        var decision = PaymentSaga.Start(Payment(), Now, new());
        decision.Step.ShouldBe(SagaStep.LedgerCredit);
        Command<CreditTopUp>(decision).AmountMinor.ShouldBe(2500);
    }
}
