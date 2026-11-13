using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Features.Refunds;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Tests;

public sealed class RefundSagaTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static PaymentSnapshot Payment() => new(Guid.NewGuid(), PaymentKind.MerchantPayment, PaymentStatus.Captured,
        SagaStep.None, 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2500, "TRY", CapturedMinor: 2000, RefundedMinor: 500);

    [Fact]
    public void R01_start_sends_RefundFunds()
    {
        var payment = Payment();
        var refund = new RefundSnapshot(Guid.NewGuid(), payment.Id, 500, RefundStatus.Processing);
        var decision = RefundSaga.Start(refund, payment);
        var command = decision.Effects.OfType<SagaEffect.SendCommand>().Single().Message.ShouldBeOfType<RefundFunds>();
        command.ShouldBe(new(OperationIds.ForRefund(refund.Id), payment.Id, refund.Id, payment.DestinationAccountId, payment.SourceAccountId!.Value, 500, "TRY"));
    }

    [Fact]
    public void R02_ledger_succeeded_completes_and_publishes_PaymentRefunded()
    {
        var payment = Payment();
        var refund = new RefundSnapshot(Guid.NewGuid(), payment.Id, 500, RefundStatus.Processing);
        var decision = RefundSaga.Decide(refund, payment, new SagaInput.LedgerSucceeded(OperationIds.ForRefund(refund.Id)), Now);
        decision.Status.ShouldBe(RefundStatus.Completed);
        decision.RefundedDelta.ShouldBe(500);
        var message = decision.Effects.OfType<SagaEffect.PublishEvent>().Single().Message.ShouldBeOfType<PaymentRefunded>();
        message.RefundId.ShouldBe(refund.Id);
        message.RefundAmountMinor.ShouldBe(500);
        message.RefundedTotalMinor.ShouldBe(1000);
        RefundSaga.Decide(refund with { Status = RefundStatus.Completed }, payment,
            new SagaInput.LedgerSucceeded(OperationIds.ForRefund(refund.Id)), Now).Ignored.ShouldBeTrue();
    }

    [Fact]
    public void R03_ledger_rejected_fails_and_frees_pending_amount()
    {
        var payment = Payment();
        var refund = new RefundSnapshot(Guid.NewGuid(), payment.Id, 500, RefundStatus.Processing);
        var decision = RefundSaga.Decide(refund, payment,
            new SagaInput.LedgerRejected(OperationIds.ForRefund(refund.Id), "InsufficientFunds"), Now);
        decision.Status.ShouldBe(RefundStatus.Failed);
        decision.FailureReason.ShouldBe("InsufficientFunds");
        decision.RefundedDelta.ShouldBe(0);
        decision.Effects.ShouldBeEmpty();
    }
}
