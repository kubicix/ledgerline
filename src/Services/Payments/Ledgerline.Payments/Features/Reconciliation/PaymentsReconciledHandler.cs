using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Reconciliation;
using Ledgerline.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.Reconciliation;

/// <summary>
/// Stamps <c>reconciled_at</c> on the payments a reconciliation run matched on all three sides (payments.md section 5).
/// One set-based statement: a payment that already has a timestamp keeps it, so a re-run's second event changes nothing,
/// and an id Payments does not know is ignored.
/// </summary>
public sealed class PaymentsReconciledHandler(PaymentsDbContext db, IClock clock) : IMessageHandler<PaymentsReconciled>
{
    public async Task HandleAsync(PaymentsReconciled message, MessageContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.PaymentIds.Count == 0) return;
        var ids = message.PaymentIds.ToArray();
        var now = clock.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE payments SET reconciled_at = {now} WHERE id = ANY({ids}) AND reconciled_at IS NULL", ct);
    }
}
