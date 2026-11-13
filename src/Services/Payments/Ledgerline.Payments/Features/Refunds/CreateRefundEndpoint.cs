using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Idempotency;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Ledgerline.Payments.Sagas;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.Refunds;

public sealed record CreateRefundRequest(long AmountMinor);

internal static class CreateRefundEndpoint
{
    internal static async Task<IResult> HandleAsync(Guid id, CreateRefundRequest request, HttpContext http,
        PaymentsDbContext db, ICurrentUser user, IClock clock, RefundRecovery recovery, CancellationToken ct)
    {
        var key = (string)http.Items[IdempotencyEndpointFilter.KeyItem]!;
        var hash = (string)http.Items[IdempotencyEndpointFilter.HashItem]!;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SagaActionAudit.LockKeyAsync(db, user.Id, key, ct);
        var payment = await db.Payments.FromSqlInterpolated($"SELECT p.*, p.xmin FROM payments p WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (payment is null || !await db.AccountRefs.AnyAsync(x => x.AccountId == payment.DestinationAccountId && x.OwnerId == user.Id, ct)) return ProblemResults.NotFound();
        var existing = await db.Refunds.AsNoTracking().SingleOrDefaultAsync(x => x.RequestedBy == user.Id && x.IdempotencyKey == key, ct);
        if (existing is not null) return existing.PaymentId == id && existing.RequestHash == hash ? Accepted(existing) : ProblemResults.IdempotencyKeyReused();
        if (await SagaActionAudit.FindAsync(db, user.Id, key, ct) is not null ||
            await db.Payments.AnyAsync(x => x.CustomerId == user.Id && x.IdempotencyKey == key, ct)) return ProblemResults.IdempotencyKeyReused();
        if (payment.Kind != PaymentKind.MerchantPayment || payment.Status != PaymentStatus.Captured) return ProblemResults.InvalidState("Refund requires a captured merchant payment.");
        if (request.AmountMinor is <= 0 or > Money.MaxAmountMinor)
            return ProblemResults.Validation(new Dictionary<string, string[]> { ["amountMinor"] = ["Refund amount is outside the supported range."] });
        var pending = await db.Refunds.Where(x => x.PaymentId == id && x.Status == RefundStatus.Processing).SumAsync(x => x.AmountMinor, ct);
        if (request.AmountMinor > payment.CapturedMinor - payment.RefundedMinor - pending) return ProblemResults.InvalidState("Refund exceeds the remaining captured amount, including pending refunds.");
        var refund = new Refund { Id = Ids.New(), PaymentId = id, AmountMinor = request.AmountMinor, Status = RefundStatus.Processing,
            RequestedBy = user.Id, IdempotencyKey = key, RequestHash = hash, CreatedAt = clock.UtcNow };
        db.Refunds.Add(refund);
        RefundAudit.Append(db, refund, "RefundStarted", refund.CreatedAt);
        recovery.Start(refund, payment);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Accepted(refund);
    }

    private static IResult Accepted(Refund refund) => Results.Accepted($"/payments/{refund.PaymentId}/refunds", RefundResponse.From(refund));
}
