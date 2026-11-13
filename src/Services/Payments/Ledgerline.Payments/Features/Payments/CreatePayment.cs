using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Idempotency;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Ledgerline.Payments.Sagas;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledgerline.Payments.Features.Payments;

internal static class CreatePayment
{
    internal static Task<IResult> HandleAsync(PaymentKind kind, Guid? source, Guid destination, long amount, string currency,
        string? cardToken, HttpContext http, PaymentsDbContext db, SagaRunner runner, ICurrentUser user, IClock clock, CancellationToken ct)
    {
        var key = (string)http.Items[IdempotencyEndpointFilter.KeyItem]!;
        var hash = (string)http.Items[IdempotencyEndpointFilter.HashItem]!;
        return IdempotentCreate.ExecuteAsync(hash,
            () => CreateAsync(kind, source, destination, amount, currency, cardToken, db, runner, user, clock, key, hash, ct),
            () => FindAsync(db, user.Id, key, ct),
            error => error is DbUpdateException { InnerException: PostgresException { SqlState: "23505" } });
    }

    private static async Task<IResult> CreateAsync(PaymentKind kind, Guid? source, Guid destination, long amount, string currency,
        string? cardToken, PaymentsDbContext db, SagaRunner runner, ICurrentUser user, IClock clock, string key, string hash, CancellationToken ct)
    {
        if (amount <= 0 || amount > Money.MaxAmountMinor) return ProblemResults.Validation(new Dictionary<string, string[]> { ["amountMinor"] = ["Amount is outside the supported range."] });
        var invalid = await ValidateAccountsAsync(kind, source, destination, currency, db, user.Id, ct);
        if (invalid is not null) return invalid;
        var payment = new Payment { Id = Ids.New(), Kind = kind, Status = PaymentStatus.Processing, CustomerId = user.Id,
            SourceAccountId = source, DestinationAccountId = destination, AmountMinor = amount, Currency = currency,
            CardToken = cardToken, IdempotencyKey = key, RequestHash = hash, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        await runner.StartAsync(payment, ct);
        return Results.Accepted("/payments/" + payment.Id, await PaymentResponse.FromAsync(payment, db, ct));
    }

    private static async Task<IResult?> ValidateAccountsAsync(PaymentKind kind, Guid? source, Guid destination,
        string currency, PaymentsDbContext db, Guid caller, CancellationToken ct)
    {
        var ownedId = kind == PaymentKind.TopUp ? destination : source;
        var owned = await db.AccountRefs.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == ownedId && x.OwnerId == caller, ct);
        if (owned is null) return ProblemResults.NotFound();
        var target = kind == PaymentKind.TopUp ? owned : await db.AccountRefs.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == destination, ct);
        if (target is null) return ProblemResults.NotFound();
        if (owned.Status != "Active" || target.Status != "Active") return ProblemResults.InvalidState("The accounts must be active.");
        if (source == destination || owned.Currency != currency || target.Currency != currency || kind == PaymentKind.TopUp && owned.Type != "CustomerWallet")
            return ProblemResults.Validation(new Dictionary<string, string[]> { ["accountId"] = ["Account type, destination or currency is invalid."] });
        return null;
    }

    private static async Task<IdempotentReplay?> FindAsync(PaymentsDbContext db, Guid customer, string key, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.CustomerId == customer && x.IdempotencyKey == key, ct);
        return payment is null ? null : new IdempotentReplay(payment.RequestHash,
            Results.Accepted("/payments/" + payment.Id, await PaymentResponse.FromAsync(payment, db, ct)));
    }
}
