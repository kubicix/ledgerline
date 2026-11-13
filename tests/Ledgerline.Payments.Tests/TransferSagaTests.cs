using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Contracts.Risk;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Tests;

public sealed class TransferSagaTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly PaymentSagaOptions Options = new() { RiskEnabled = true, BankEnabled = true };
    private static PaymentSnapshot Payment(SagaStep step = SagaStep.None) => new(Guid.NewGuid(), PaymentKind.Transfer,
        PaymentStatus.Processing, step, 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2500, "TRY");

    [Fact]
    public void X01_start_sends_EvaluateRisk() => PaymentSaga.Start(Payment(), Now, Options).Effects
        .OfType<SagaEffect.SendCommand>().Single().Message.ShouldBeOfType<EvaluateRisk>();

    [Fact]
    public void X02_risk_approved_sends_TransferFunds()
    {
        var payment = Payment(SagaStep.RiskCheck);
        var decision = PaymentSaga.Decide(payment, new SagaInput.RiskApproved(), Now, Options);
        decision.Step.ShouldBe(SagaStep.LedgerTransfer);
        decision.Effects.OfType<SagaEffect.SendCommand>().Single().Message.ShouldBeOfType<TransferFunds>()
            .OperationId.ShouldBe(OperationIds.For(payment.Id, SagaStep.LedgerTransfer));
    }

    [Fact]
    public void X03_risk_rejected_ends_Rejected() => PaymentSaga.Decide(Payment(SagaStep.RiskCheck),
        new SagaInput.RiskRejected("DailyAmountLimit"), Now, Options).Status.ShouldBe(PaymentStatus.Rejected);

    [Fact]
    public void X04_ledger_succeeded_ends_Completed()
    {
        var payment = Payment(SagaStep.LedgerTransfer);
        var decision = PaymentSaga.Decide(payment, new SagaInput.LedgerSucceeded(OperationIds.For(payment.Id, payment.Step)), Now, Options);
        decision.Status.ShouldBe(PaymentStatus.Completed);
        decision.Effects.OfType<SagaEffect.PublishEvent>().Single().Message.ShouldBeOfType<PaymentCompleted>();
    }

    [Fact]
    public void X05_ledger_rejected_ends_Failed()
    {
        var payment = Payment(SagaStep.LedgerTransfer);
        var decision = PaymentSaga.Decide(payment, new SagaInput.LedgerRejected(OperationIds.For(payment.Id, payment.Step), "AccountFrozen"), Now, Options);
        decision.Status.ShouldBe(PaymentStatus.Failed);
        decision.FailureReason.ShouldBe("AccountFrozen");
    }
}
