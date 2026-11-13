using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Tests;

public sealed class OperationIdsTests
{
    [Fact]
    public void OperationIds_are_deterministic_and_differ_per_step()
    {
        var paymentId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var transfer = OperationIds.For(paymentId, SagaStep.LedgerTransfer);
        transfer.ShouldBe(Guid.Parse("ce2fcca9-23f1-5cc9-a8f0-9185e1de3dff"));
        OperationIds.For(paymentId, SagaStep.LedgerTransfer).ShouldBe(transfer);
        OperationIds.For(paymentId, SagaStep.LedgerCredit).ShouldBe(Guid.Parse("8054a99d-66fc-5c8c-8559-f3bd2f01ceb4"));
        OperationIds.For(Guid.NewGuid(), SagaStep.LedgerTransfer).ShouldNotBe(transfer);
    }
}
