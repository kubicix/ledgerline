using Ledgerline.BankSimulator.Infrastructure;

namespace Ledgerline.BankSimulator.Features;

public static class CaptureEndpoint
{
    public static Task<IResult> HandleAsync(string id, HttpContext http, BankDbContext db, CancellationToken ct) =>
        BankIdempotency.ExecuteAsync(http, "Capture", db, async () =>
        {
            var authorization = await BankIdempotency.LockAuthorizationAsync(db, id, ct);
            if (authorization is null) return BankIdempotency.Problem(http, 404, "not-found", "Authorization not found");
            if (authorization.Status != "Authorized") return BankIdempotency.Problem(http, 409, "invalid-state", "Capture requires Authorized state");
            if (authorization.CardToken == "tok_capture_fails")
                return BankIdempotency.Response(402, new { status = "Declined", declineCode = "capture_declined" }, authorization);
            authorization.CaptureId = "cap_" + Guid.NewGuid().ToString("N");
            authorization.Status = "Captured";
            return BankIdempotency.Response(200, new { captureId = authorization.CaptureId, status = "Captured", capturedAt = DateTimeOffset.UtcNow }, authorization);
        }, ct);
}
