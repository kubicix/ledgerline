using Ledgerline.BankSimulator.Infrastructure;

namespace Ledgerline.BankSimulator.Features;

public static class VoidEndpoint
{
    public static Task<IResult> HandleAsync(string id, HttpContext http, BankDbContext db, CancellationToken ct) =>
        BankIdempotency.ExecuteAsync(http, "Void", db, async () =>
        {
            var authorization = await BankIdempotency.LockAuthorizationAsync(db, id, ct);
            if (authorization is null) return BankIdempotency.Problem(http, 404, "not-found", "Authorization not found");
            if (authorization.Status != "Authorized") return BankIdempotency.Problem(http, 409, "invalid-state", "Void requires Authorized state");
            authorization.Status = "Voided";
            return BankIdempotency.Response(200, new { status = "Voided" }, authorization);
        }, ct);
}
