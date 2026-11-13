using Ledgerline.BankSimulator.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.BankSimulator.Features;

public static class RefundEndpoint
{
    public static Task<IResult> HandleAsync(string id, HttpContext http, BankDbContext db, CancellationToken ct) =>
        BankIdempotency.ExecuteAsync(http, "Refund", db, async () =>
        {
            var authorization = await db.Authorizations.FromSqlInterpolated(
                $"SELECT * FROM bank_authorizations WHERE capture_id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (authorization is null) return BankIdempotency.Problem(http, 404, "not-found", "Capture not found");
            if (authorization.Status != "Captured") return BankIdempotency.Problem(http, 409, "invalid-state", "Refund requires Captured state");
            authorization.Status = "Refunded";
            var refundId = "ref_" + Guid.NewGuid().ToString("N");
            var refundedAt = BankIdempotency.Timestamp();
            return BankIdempotency.Response(200, new { refundId, status = "Refunded", refundedAt }, authorization, refundId, refundedAt);
        }, ct);
}
