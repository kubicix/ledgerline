using System.Text.Json;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Ledgerline.Payments.Sagas;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.Refunds;

public sealed record RefundSnapshot(Guid Id, Guid PaymentId, long AmountMinor, RefundStatus Status, string? FailureReason = null);
public sealed record RefundDecision(RefundStatus Status, IReadOnlyList<SagaEffect> Effects, string? FailureReason,
    long RefundedDelta = 0, bool Ignored = false);

public static class RefundSaga
{
    public static RefundDecision Start(RefundSnapshot refund, PaymentSnapshot payment)
    {
        ArgumentNullException.ThrowIfNull(refund);
        ArgumentNullException.ThrowIfNull(payment);
        return new(RefundStatus.Processing, [new SagaEffect.SendCommand(new RefundFunds(OperationIds.ForRefund(refund.Id),
            payment.Id, refund.Id, payment.DestinationAccountId, payment.SourceAccountId!.Value, refund.AmountMinor, payment.Currency))], null);
    }

    public static RefundDecision Decide(RefundSnapshot refund, PaymentSnapshot payment, SagaInput input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(refund);
        ArgumentNullException.ThrowIfNull(payment);
        if (refund.Status != RefundStatus.Processing) return new(refund.Status, [], refund.FailureReason, Ignored: true);
        var operation = OperationIds.ForRefund(refund.Id);
        return input switch
        {
            SagaInput.LedgerSucceeded success when success.OperationId == operation => new(RefundStatus.Completed,
                [new SagaEffect.PublishEvent(new PaymentRefunded(payment.Id, payment.Kind.ToString(), payment.CustomerId,
                    payment.AmountMinor, payment.Currency, payment.SourceAccountId, payment.DestinationAccountId, now,
                    refund.Id, refund.AmountMinor, checked(payment.RefundedMinor + refund.AmountMinor)))], null, refund.AmountMinor),
            SagaInput.LedgerRejected rejected when rejected.OperationId == operation => new(RefundStatus.Failed, [], rejected.Reason),
            _ => new(refund.Status, [], refund.FailureReason, Ignored: true)
        };
    }
}

public sealed class RefundRunner(PaymentsDbContext db, IOutbox outbox, IClock clock)
{
    internal async Task<bool> TryApplyAsync(Guid paymentId, Guid operationId, SagaInput input, MessageContext context, CancellationToken ct)
    {
        var candidates = await db.Refunds.AsNoTracking().Where(x => x.PaymentId == paymentId).ToListAsync(ct);
        var candidate = candidates.SingleOrDefault(x => OperationIds.ForRefund(x.Id) == operationId);
        if (candidate is null) return false;
        var payment = await db.Payments.FromSqlInterpolated($"SELECT p.*, p.xmin FROM payments p WHERE id = {paymentId} FOR UPDATE").SingleAsync(ct);
        var refund = await db.Refunds.SingleAsync(x => x.Id == candidate.Id, ct);
        var now = clock.UtcNow;
        var decision = RefundSaga.Decide(new(refund.Id, paymentId, refund.AmountMinor, refund.Status, refund.FailureReason), payment.Snapshot(), input, now);
        if (decision.Ignored) return true;
        refund.Status = decision.Status;
        refund.FailureReason = decision.FailureReason;
        payment.RefundedMinor = checked(payment.RefundedMinor + decision.RefundedDelta);
        payment.UpdatedAt = now;
        RefundAudit.Append(db, refund, decision.Status == RefundStatus.Completed ? "RefundCompleted" : "RefundFailed", now);
        foreach (var effect in decision.Effects)
            if (effect is SagaEffect.PublishEvent { Message: PaymentRefunded message }) outbox.Enqueue(message, paymentId, context.MessageId);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

internal static class RefundAudit
{
    internal static void Append(PaymentsDbContext db, Refund refund, string outcome, DateTimeOffset at) =>
        db.SagaSteps.Add(new SagaStepRow { PaymentId = refund.PaymentId, Step = SagaStep.None, Outcome = outcome,
            Detail = JsonSerializer.Serialize(new { refundId = refund.Id, reason = refund.FailureReason }, JsonSerializerOptions.Web), At = at });
}
