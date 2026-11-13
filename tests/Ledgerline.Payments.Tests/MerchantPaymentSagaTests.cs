using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Contracts.Risk;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Tests;

public sealed class MerchantPaymentSagaTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly PaymentSagaOptions Options = new() { RiskEnabled = true, BankEnabled = true };
    private static PaymentSnapshot Payment(SagaStep step = SagaStep.None, PaymentStatus status = PaymentStatus.Processing) =>
        new(Guid.NewGuid(), PaymentKind.MerchantPayment, status, step, 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2500, "TRY", CaptureMode: CaptureMode.Manual, AuthorizationExpiresAt: Now.AddDays(7));
    private static SagaDecision Decide(PaymentSnapshot payment, SagaInput input) => PaymentSaga.Decide(payment, input, Now, Options);
    private static SagaInput.LedgerSucceeded Success(PaymentSnapshot payment) => new(OperationIds.For(payment.Id, payment.Step));
    private static T Command<T>(SagaDecision decision) => decision.Effects.OfType<SagaEffect.SendCommand>().Single().Message.ShouldBeOfType<T>();

    [Fact]
    public void M01_start_sends_EvaluateRisk() => Command<EvaluateRisk>(PaymentSaga.Start(Payment(), Now, Options)).Kind.ShouldBe("MerchantPayment");

    [Fact]
    public void M02_risk_approved_sends_ReserveFunds()
    {
        var payment = Payment(SagaStep.RiskCheck);
        var decision = Decide(payment, new SagaInput.RiskApproved());
        decision.Step.ShouldBe(SagaStep.LedgerReserve);
        Command<ReserveFunds>(decision).ShouldBe(new(OperationIds.For(payment.Id, SagaStep.LedgerReserve), payment.Id, payment.SourceAccountId!.Value, 2500, "TRY"));
    }

    [Fact]
    public void M03_risk_rejected_ends_Rejected() => Decide(Payment(SagaStep.RiskCheck), new SagaInput.RiskRejected("VelocityLimit")).Status.ShouldBe(PaymentStatus.Rejected);

    [Fact]
    public void M04_manual_reserve_succeeded_ends_Authorized_with_expiry()
    {
        var payment = Payment(SagaStep.LedgerReserve);
        var decision = Decide(payment, Success(payment));
        decision.Status.ShouldBe(PaymentStatus.Authorized);
        decision.Step.ShouldBe(SagaStep.None);
        decision.Effects.OfType<SagaEffect.SetAuthorizationExpiresAt>().Single().At.ShouldBe(Now.AddDays(7));
        decision.Effects.OfType<SagaEffect.ScheduleTimeout>().Single().ShouldBe(new(SagaStep.None, TimeSpan.FromDays(7)));
        decision.Effects.OfType<SagaEffect.PublishEvent>().Single().Message.ShouldBeOfType<PaymentAuthorized>().AuthorizationExpiresAt.ShouldBe(Now.AddDays(7));
    }

    [Fact]
    public void M05_automatic_reserve_succeeded_starts_LedgerCapture()
    {
        var payment = Payment(SagaStep.LedgerReserve) with { CaptureMode = CaptureMode.Automatic };
        var decision = Decide(payment, Success(payment));
        decision.Status.ShouldBe(PaymentStatus.Capturing);
        Command<CaptureFunds>(decision).CaptureAmountMinor.ShouldBe(2500);
    }

    [Fact]
    public void M06_reserve_rejected_ends_Failed()
    {
        var payment = Payment(SagaStep.LedgerReserve);
        Decide(payment, new SagaInput.LedgerRejected(OperationIds.For(payment.Id, payment.Step), "InsufficientFunds"))
            .Status.ShouldBe(PaymentStatus.Failed);
    }

    [Fact]
    public void M07_capture_requested_starts_LedgerCapture()
    {
        var decision = Decide(Payment(status: PaymentStatus.Authorized), new SagaInput.CaptureRequested(1000));
        decision.Status.ShouldBe(PaymentStatus.Capturing);
        decision.Effects.OfType<SagaEffect.SetCaptureRequestedMinor>().Single().AmountMinor.ShouldBe(1000);
        Command<CaptureFunds>(decision).CaptureAmountMinor.ShouldBe(1000);
    }

    [Fact]
    public void M08_capture_succeeded_ends_Captured()
    {
        var payment = Payment(SagaStep.LedgerCapture, PaymentStatus.Capturing) with { CaptureRequestedMinor = 1000 };
        var decision = Decide(payment, Success(payment));
        decision.Status.ShouldBe(PaymentStatus.Captured);
        decision.Effects.OfType<SagaEffect.SetCapturedMinor>().Single().AmountMinor.ShouldBe(1000);
        decision.Effects.OfType<SagaEffect.PublishEvent>().Single().Message.ShouldBeOfType<PaymentCaptured>().CapturedMinor.ShouldBe(1000);
    }

    [Fact]
    public void M09_capture_rejected_returns_Authorized_with_reason()
    {
        var payment = Payment(SagaStep.LedgerCapture, PaymentStatus.Capturing) with { AuthorizationExpiresAt = Now.AddDays(1) };
        var decision = Decide(payment, new SagaInput.LedgerRejected(OperationIds.For(payment.Id, payment.Step), "AccountFrozen"));
        decision.Status.ShouldBe(PaymentStatus.Authorized);
        decision.FailureReason.ShouldBe("AccountFrozen");
        decision.Effects.OfType<SagaEffect.ScheduleTimeout>().Single().After.ShouldBe(TimeSpan.FromDays(1));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void M10_void_or_expiry_starts_LedgerRelease(bool expiry)
    {
        var decision = Decide(Payment(status: PaymentStatus.Authorized), expiry ? new SagaInput.AuthorizationExpired() : new SagaInput.VoidRequested());
        decision.Status.ShouldBe(PaymentStatus.Voiding);
        decision.Step.ShouldBe(SagaStep.LedgerRelease);
        Command<ReleaseFunds>(decision).PaymentId.ShouldNotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("Requested")] [InlineData("Expired")]
    public void M11_release_succeeded_ends_Voided(string reason)
    {
        var payment = Payment(SagaStep.LedgerRelease, PaymentStatus.Voiding) with { FailureReason = reason };
        var decision = Decide(payment, Success(payment));
        decision.Status.ShouldBe(PaymentStatus.Voided);
        decision.Effects.OfType<SagaEffect.PublishEvent>().Single().Message.ShouldBeOfType<PaymentVoided>().Reason.ShouldBe(reason);
    }

    [Fact]
    public void M12_release_rejected_ends_ManualReview()
    {
        var payment = Payment(SagaStep.LedgerRelease, PaymentStatus.Voiding);
        Decide(payment, new SagaInput.LedgerRejected(OperationIds.For(payment.Id, payment.Step), "HoldNotActive"))
            .Status.ShouldBe(PaymentStatus.ManualReview);
    }

    [Theory]
    [InlineData(PaymentStatus.Processing)] [InlineData(PaymentStatus.Capturing)] [InlineData(PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Voided)] [InlineData(PaymentStatus.Failed)]
    public void Capture_request_when_not_Authorized_is_rejected_as_invalid_state(PaymentStatus status) =>
        Decide(Payment(status: status), new SagaInput.CaptureRequested(1000)).InvalidState.ShouldBeTrue();

    [Theory]
    [InlineData(2501)] [InlineData(0)] [InlineData(-1)]
    public void Capture_amount_above_authorized_is_invalid(long amount) =>
        Decide(Payment(status: PaymentStatus.Authorized), new SagaInput.CaptureRequested(amount)).InvalidState.ShouldBeTrue();

    [Fact]
    public void Expiry_in_final_state_is_ignored() => Decide(Payment(status: PaymentStatus.Captured), new SagaInput.AuthorizationExpired()).Ignored.ShouldBeTrue();

    [Fact]
    public void Automatic_capture_mode_goes_straight_to_LedgerCapture() => M05_automatic_reserve_succeeded_starts_LedgerCapture();
}
