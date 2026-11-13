using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Tests;

public sealed class PaymentSagaTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static PaymentSnapshot Payment(PaymentKind kind = PaymentKind.Transfer) => new(
        Guid.NewGuid(), kind, PaymentStatus.Processing, SagaStep.None, 1, Guid.NewGuid(),
        kind == PaymentKind.TopUp ? null : Guid.NewGuid(), Guid.NewGuid(), 2500, "TRY");

    [Fact]
    public void Transfer_start_sends_TransferFunds_and_schedules_30s_timeout()
    {
        var payment = Payment();
        var decision = PaymentSaga.Start(payment, Now, new PaymentSagaOptions());
        decision.Status.ShouldBe(PaymentStatus.Processing);
        decision.Step.ShouldBe(SagaStep.LedgerTransfer);
        var command = decision.Effects.OfType<SagaEffect.SendCommand>().Single().Message.ShouldBeOfType<TransferFunds>();
        command.ShouldBe(new TransferFunds(OperationIds.For(payment.Id, SagaStep.LedgerTransfer), payment.Id,
            payment.SourceAccountId!.Value, payment.DestinationAccountId, 2500, "TRY"));
        decision.Effects.OfType<SagaEffect.ScheduleTimeout>().Single().ShouldBe(new(SagaStep.LedgerTransfer, TimeSpan.FromSeconds(30)));
        decision.Ignored.ShouldBeFalse();
    }

    [Fact]
    public void Transfer_ledger_succeeded_completes_and_publishes_PaymentCompleted()
    {
        var payment = Payment() with { Step = SagaStep.LedgerTransfer };
        var decision = PaymentSaga.Decide(payment, new SagaInput.LedgerSucceeded(OperationIds.For(payment.Id, payment.Step)), Now);
        decision.Status.ShouldBe(PaymentStatus.Completed);
        decision.Step.ShouldBe(SagaStep.None);
        decision.FailureReason.ShouldBeNull();
        decision.Effects.OfType<SagaEffect.CancelTimeouts>().Count().ShouldBe(1);
        var completed = decision.Effects.OfType<SagaEffect.PublishEvent>().Single().Message.ShouldBeOfType<PaymentCompleted>();
        completed.ShouldBe(new PaymentCompleted(payment.Id, "Transfer", payment.CustomerId, 2500, "TRY",
            payment.SourceAccountId, payment.DestinationAccountId, Now, null));
    }

    [Fact]
    public void Transfer_ledger_rejected_fails_with_reason_and_publishes_PaymentFailed()
    {
        var payment = Payment() with { Step = SagaStep.LedgerTransfer };
        var decision = PaymentSaga.Decide(payment, new SagaInput.LedgerRejected(OperationIds.For(payment.Id, payment.Step), "InsufficientFunds"), Now);
        decision.Status.ShouldBe(PaymentStatus.Failed);
        decision.Step.ShouldBe(SagaStep.None);
        decision.FailureReason.ShouldBe("InsufficientFunds");
        decision.Effects.OfType<SagaEffect.CancelTimeouts>().Count().ShouldBe(1);
        var failed = decision.Effects.OfType<SagaEffect.PublishEvent>().Single().Message.ShouldBeOfType<PaymentFailed>();
        failed.Status.ShouldBe("Failed");
        failed.ReasonCode.ShouldBe("InsufficientFunds");
        failed.PaymentId.ShouldBe(payment.Id);
        failed.OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public void TopUp_start_without_bank_sends_CreditTopUp()
    {
        var payment = Payment(PaymentKind.TopUp);
        var decision = PaymentSaga.Start(payment, Now);
        decision.Step.ShouldBe(SagaStep.LedgerCredit);
        decision.Effects.OfType<SagaEffect.SendCommand>().Single().Message.ShouldBe(new CreditTopUp(
            OperationIds.For(payment.Id, SagaStep.LedgerCredit), payment.Id, payment.DestinationAccountId, 2500, "TRY"));
        decision.Effects.OfType<SagaEffect.ScheduleTimeout>().Single().After.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void TopUp_ledger_succeeded_completes()
    {
        var payment = Payment(PaymentKind.TopUp) with { Step = SagaStep.LedgerCredit };
        var decision = PaymentSaga.Decide(payment, new SagaInput.LedgerSucceeded(OperationIds.For(payment.Id, payment.Step)), Now);
        decision.Status.ShouldBe(PaymentStatus.Completed);
        var completed = decision.Effects.OfType<SagaEffect.PublishEvent>().Single().Message.ShouldBeOfType<PaymentCompleted>();
        completed.Kind.ShouldBe("TopUp");
        completed.SourceAccountId.ShouldBeNull();
        completed.BankCaptureId.ShouldBeNull();
    }

    public static IEnumerable<object[]> FinalInputs()
    {
        PaymentStatus[] statuses = [PaymentStatus.Completed, PaymentStatus.Voided, PaymentStatus.Compensated,
            PaymentStatus.Rejected, PaymentStatus.Failed, PaymentStatus.ManualReview, PaymentStatus.Captured];
        SagaInput[] inputs = [new SagaInput.RiskApproved(), new SagaInput.RiskRejected("RiskLimit"),
            new SagaInput.LedgerSucceeded(Guid.NewGuid()), new SagaInput.LedgerRejected(Guid.NewGuid(), "Frozen"),
            new SagaInput.BankAuthorized("auth"), new SagaInput.BankDeclined("Declined"), new SagaInput.BankCaptured("capture"),
            new SagaInput.BankCaptureDeclined("Declined"), new SagaInput.BankCompensated(), new SagaInput.CaptureRequested(100),
            new SagaInput.VoidRequested(), new SagaInput.AuthorizationExpired(), new SagaInput.StepTimedOut(SagaStep.LedgerTransfer)];
        foreach (var status in statuses)
        foreach (var input in inputs) yield return [status, input];
    }

    [Theory]
    [MemberData(nameof(FinalInputs))]
    public void Decide_ignores_inputs_in_final_states(PaymentStatus status, SagaInput input)
    {
        var payment = Payment() with { Status = status, FailureReason = "PreviousReason" };
        var decision = PaymentSaga.Decide(payment, input, Now);
        decision.Ignored.ShouldBeTrue();
        decision.Effects.ShouldBeEmpty();
        decision.Status.ShouldBe(status);
        decision.Step.ShouldBe(payment.Step);
        decision.FailureReason.ShouldBe("PreviousReason");
    }

    [Fact]
    public void Decide_ignores_reply_for_other_step()
    {
        var payment = Payment() with { Step = SagaStep.LedgerTransfer };
        var reply = new SagaInput.LedgerSucceeded(OperationIds.For(payment.Id, SagaStep.LedgerCredit));
        var decision = PaymentSaga.Decide(payment, reply, Now);
        decision.Ignored.ShouldBeTrue();
        decision.Effects.ShouldBeEmpty();
        PaymentSaga.Decide(payment, new SagaInput.StepTimedOut(SagaStep.BankCapture), Now).Ignored.ShouldBeTrue();
    }

    [Fact]
    public void StepTimedOut_resends_same_command_with_same_operation_id()
    {
        var payment = Payment();
        var start = PaymentSaga.Start(payment, Now);
        var retry = PaymentSaga.Decide(payment with { Step = start.Step, StepAttempt = 2 }, new SagaInput.StepTimedOut(start.Step), Now);
        retry.Status.ShouldBe(PaymentStatus.Processing);
        retry.Step.ShouldBe(start.Step);
        retry.Effects.OfType<SagaEffect.SendCommand>().Single().ShouldBe(start.Effects.OfType<SagaEffect.SendCommand>().Single());
        retry.Effects.OfType<SagaEffect.ScheduleTimeout>().Single().After.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Fifth_ledger_timeout_enters_manual_review_and_publishes_failure()
    {
        var payment = Payment() with { Step = SagaStep.LedgerTransfer, StepAttempt = 5 };
        var decision = PaymentSaga.Decide(payment, new SagaInput.StepTimedOut(payment.Step), Now);
        decision.Status.ShouldBe(PaymentStatus.ManualReview);
        decision.Effects.OfType<SagaEffect.SendCommand>().ShouldBeEmpty();
        decision.Effects.OfType<SagaEffect.ScheduleTimeout>().ShouldBeEmpty();
        decision.Effects.OfType<SagaEffect.CancelTimeouts>().Count().ShouldBe(1);
        decision.Effects.OfType<SagaEffect.PublishEvent>().Single().Message.ShouldBeOfType<PaymentFailed>().Status.ShouldBe("ManualReview");
    }
}
