using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Idempotency;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Ledgerline.Payments.Sagas;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.Payments;

internal static class PaymentActions
{
    internal static async Task<IResult> ExecuteAsync(Guid id, bool capture, long? amount, HttpContext http,
        PaymentsDbContext db, SagaRunner runner, ICurrentUser user, CancellationToken ct)
    {
        var key = (string)http.Items[IdempotencyEndpointFilter.KeyItem]!;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SagaActionAudit.LockKeyAsync(db, user.Id, key, ct);
        var payment = await db.Payments.FromSqlInterpolated($"SELECT p.*, p.xmin FROM payments p WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (payment is null || payment.Kind != PaymentKind.MerchantPayment) return ProblemResults.NotFound();
        if (!(user.IsInRole("admin") && !capture) &&
            !await db.AccountRefs.AnyAsync(x => x.AccountId == payment.DestinationAccountId && x.OwnerId == user.Id, ct)) return ProblemResults.NotFound();
        var hash = (string)http.Items[IdempotencyEndpointFilter.HashItem]!;
        var previous = await SagaActionAudit.FindAsync(db, user.Id, key, ct);
        if (previous is not null) return previous.PaymentId == id && previous.Hash == hash
            ? await AcceptedAsync(payment, db, ct) : ProblemResults.IdempotencyKeyReused();
        if (await db.Payments.AnyAsync(x => x.CustomerId == user.Id && x.IdempotencyKey == key, ct)) return ProblemResults.IdempotencyKeyReused();
        if (await db.Refunds.AnyAsync(x => x.RequestedBy == user.Id && x.IdempotencyKey == key, ct)) return ProblemResults.IdempotencyKeyReused();
        if (payment.Status != PaymentStatus.Authorized) return ProblemResults.InvalidState("The payment must be Authorized.");
        var captureAmount = amount ?? payment.AmountMinor;
        if (capture && (captureAmount <= 0 || captureAmount > payment.AmountMinor))
            return ProblemResults.Validation(new Dictionary<string, string[]> { ["amountMinor"] = ["Capture must be positive and within the authorized amount."] });
        var operation = capture ? OperationIds.ForCapture(id, key) : (Guid?)null;
        SagaInput input = capture ? new SagaInput.CaptureRequested(captureAmount, operation) : new SagaInput.VoidRequested();
        var decision = await runner.ApplyAndGetDecisionAsync(id, input, null, ct);
        if (decision.InvalidState) return ProblemResults.InvalidState("The payment must be Authorized.");
        var started = db.ChangeTracker.Entries<SagaStepRow>().Single(x => x.Entity.PaymentId == id && x.Entity.Outcome == "Started").Entity;
        started.Detail = new SagaActionAudit(id, key, hash, user.Id, operation).Serialize();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        runner.OnCommitted();
        return await AcceptedAsync(payment, db, ct);
    }

    private static async Task<IResult> AcceptedAsync(Payment payment, PaymentsDbContext db, CancellationToken ct) =>
        Results.Accepted("/payments/" + payment.Id, await PaymentResponse.FromAsync(payment, db, ct));
}
