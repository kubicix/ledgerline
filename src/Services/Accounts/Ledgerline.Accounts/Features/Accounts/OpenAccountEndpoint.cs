using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Idempotency;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using AccountOpenedMessage = Ledgerline.Contracts.Accounts.AccountOpened;

namespace Ledgerline.Accounts.Features.Accounts;

public static class AccountsEndpoints
{
    public static WebApplication MapAccountsEndpoints(this WebApplication app)
    {
        app.MapPost("/accounts", OpenAccountEndpoint.HandleAsync).RequireAuthorization(policy => policy.RequireRole("customer", "merchant")).RequireIdempotencyKey();
        app.MapGet("/accounts", ListAccountsEndpoint.HandleAsync).RequireAuthorization();
        app.MapGet("/accounts/{id:guid}", GetAccountEndpoint.HandleAsync).RequireAuthorization();
        app.MapGet("/accounts/{id:guid}/entries", ListEntriesEndpoint.HandleAsync).RequireAuthorization();
        app.MapPost("/accounts/{id:guid}/freeze", FreezeEndpoint.HandleAsync).RequireAuthorization(Policies.Admin).RequireIdempotencyKey();
        app.MapPost("/accounts/{id:guid}/unfreeze", UnfreezeEndpoint.HandleAsync).RequireAuthorization(Policies.Admin).RequireIdempotencyKey();
        return app;
    }
}

public sealed record OpenAccountRequest(string Type, string Currency);

internal static class OpenAccountEndpoint
{
    internal static async Task<IResult> HandleAsync(OpenAccountRequest request, HttpContext http, AccountsDbContext db,
        ICurrentUser user, IClock clock, IOutbox outbox, IConfiguration configuration, CancellationToken ct)
    {
        var key = (string)http.Items[IdempotencyEndpointFilter.KeyItem]!;
        var hash = (string)http.Items[IdempotencyEndpointFilter.HashItem]!;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AccountRequestKey.LockAsync(db, user.Id, key, ct);
        if (await db.StatusRequests.AnyAsync(x => x.CallerId == user.Id && x.Key == key, ct)) return ProblemResults.IdempotencyKeyReused();
        try
        {
            var result = await IdempotentCreate.ExecuteAsync(hash,
                () => CreateAsync(request, db, user, clock, outbox, configuration, key, hash, ct),
                () => FindExistingAsync(db, user.Id, key, ct), IsUniqueViolation);
            if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 }) await transaction.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            return ProblemResults.InvalidState("An account with this type and currency already exists.");
        }
    }

    private static async Task<IResult> CreateAsync(OpenAccountRequest request, AccountsDbContext db,
        ICurrentUser user, IClock clock, IOutbox outbox, IConfiguration configuration, string key, string hash, CancellationToken ct)
    {
        var errors = Validate(request, user, configuration);
        if (errors.Count != 0) return ProblemResults.Validation(errors);
        var account = new Account
        {
            Id = Ids.New(), OwnerId = user.Id, Type = request.Type, Currency = request.Currency, Status = "Active",
            Version = 1, ContactEmail = user.Email, IdempotencyKey = key, RequestHash = hash, OpenedAt = clock.UtcNow
        };
        db.Accounts.Add(account);
        AppendOpened(db, account);
        outbox.Enqueue(new AccountOpenedMessage(account.Id, account.OwnerId, account.Type, account.Currency, account.ContactEmail, account.OpenedAt), account.Id);
        await db.SaveChangesAsync(ct);
        return Results.Created("/accounts/" + account.Id, AccountResponse.From(account));
    }

    private static bool IsUniqueViolation(Exception error) => error is DbUpdateException { InnerException: PostgresException { SqlState: "23505" } };

    private static async Task<IdempotentReplay?> FindExistingAsync(AccountsDbContext db, Guid owner, string key, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.OwnerId == owner && a.IdempotencyKey == key, ct);
        if (account is null) return null;
        var held = await db.Holds.Where(h => h.AccountId == account.Id && h.Status == "Active").SumAsync(h => h.AmountMinor, ct);
        return new IdempotentReplay(account.RequestHash ?? "", Results.Created("/accounts/" + account.Id, AccountResponse.From(account, held)));
    }

    private static Dictionary<string, string[]> Validate(OpenAccountRequest request, ICurrentUser user, IConfiguration configuration)
    {
        var errors = new Dictionary<string, string[]>();
        var validType = request.Type == "CustomerWallet" && user.IsInRole("customer") || request.Type == "MerchantWallet" && user.IsInRole("merchant");
        if (!validType) errors["type"] = ["Account type is not available for this caller."];
        var currencies = configuration.GetSection("Ledger:Currencies").Get<string[]>() ?? ["TRY", "EUR", "USD"];
        if (!currencies.Contains(request.Currency, StringComparer.Ordinal)) errors["currency"] = ["Unsupported currency."];
        return errors;
    }

    private static void AppendOpened(AccountsDbContext db, Account account)
    {
        db.Events.Add(new LedgerEvent
        {
            StreamId = account.Id, Version = 1, Type = "account-opened.v1", OccurredAt = account.OpenedAt,
            Data = EnvelopeSerializer.ToPayload(new Ledgerline.Accounts.Ledger.AccountOpened(account.OwnerId, account.Type, null, account.Currency, account.ContactEmail)),
            Metadata = EnvelopeSerializer.ToPayload(new { CorrelationId = account.Id, CausationId = (Guid?)null,
                OperationId = (Guid?)null, account.IdempotencyKey, account.RequestHash })
        });
    }
}
