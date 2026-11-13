using Ledgerline.BankSimulator.Infrastructure;
using Ledgerline.BankSimulator.Infrastructure.Entities;
using Ledgerline.BuildingBlocks;

namespace Ledgerline.BankSimulator.Features;

public sealed record AuthorizeRequest(long AmountMinor, string Currency, string CardToken, string Reference);

public static class AuthorizeEndpoint
{
    public static void MapBankEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("").AddEndpointFilter<ApiKeyFilter>();
        group.MapPost("/authorizations", HandleAsync).WithMetadata(new BankOperationMetadata("Authorize"));
        group.MapPost("/authorizations/{id}/capture", CaptureEndpoint.HandleAsync).WithMetadata(new BankOperationMetadata("Capture"));
        group.MapPost("/authorizations/{id}/void", VoidEndpoint.HandleAsync).WithMetadata(new BankOperationMetadata("Void"));
        group.MapPost("/captures/{id}/refund", RefundEndpoint.HandleAsync).WithMetadata(new BankOperationMetadata("Refund"));
        group.MapGet("/operations/{idempotencyKey}", LookupEndpoint.HandleAsync).WithMetadata(new BankOperationMetadata("Lookup"));
        group.MapPut("/faults", FaultsEndpoint.Set);
        group.MapDelete("/faults", FaultsEndpoint.Clear);
        group.MapGet("/settlements/{date}", SettlementEndpoint.HandleAsync);
        group.MapPut("/settlement-tampering", (SettlementTamperingRequest request, SettlementTampering tampering) => tampering.Set(request));
    }

    public static Task<IResult> HandleAsync(AuthorizeRequest request, HttpContext http, BankDbContext db, CancellationToken ct) =>
        BankIdempotency.ExecuteAsync(http, "Authorize", db, () => Task.FromResult(Authorize(request, http, db)), ct);

    private static BankOperation Authorize(AuthorizeRequest request, HttpContext http, BankDbContext db)
    {
        if (request.AmountMinor is <= 0 or > Money.MaxAmountMinor || request.Currency is null ||
            request.Currency.Length != 3 || request.Currency.Any(c => c is < 'A' or > 'Z') || string.IsNullOrWhiteSpace(request.Reference))
            return BankIdempotency.Problem(http, 400, "validation-failed", "Invalid amount, currency or reference");
        if (request.CardToken is "tok_declined" or "tok_insufficient")
            return BankIdempotency.Response(402, new { status = "Declined", declineCode = request.CardToken == "tok_declined" ? "do_not_honor" : "insufficient_funds" });
        if (request.CardToken is not ("tok_ok" or "tok_capture_fails"))
            return BankIdempotency.Problem(http, 400, "validation-failed", "Unknown card token");
        var authorization = new BankAuthorization
        {
            Id = "auth_" + Guid.NewGuid().ToString("N"), Reference = request.Reference, AmountMinor = request.AmountMinor,
            Currency = request.Currency, CardToken = request.CardToken, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Authorizations.Add(authorization);
        return BankIdempotency.Response(201, new { authorizationId = authorization.Id, status = "Authorized" }, authorization);
    }
}
