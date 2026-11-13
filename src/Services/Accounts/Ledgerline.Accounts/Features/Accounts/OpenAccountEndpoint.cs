using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using AccountOpenedMessage = Ledgerline.Contracts.Accounts.AccountOpened;

namespace Ledgerline.Accounts.Features.Accounts;

public static class AccountsEndpoints
{
    public static WebApplication MapAccountsEndpoints(this WebApplication app)
    {
        app.MapPost("/accounts", OpenAccountEndpoint.HandleAsync).RequireAuthorization(policy => policy.RequireRole("customer", "merchant"));
        app.MapGet("/accounts", ListAccountsEndpoint.HandleAsync).RequireAuthorization();
        app.MapGet("/accounts/{id:guid}", GetAccountEndpoint.HandleAsync).RequireAuthorization();
        app.MapGet("/accounts/{id:guid}/entries", ListEntriesEndpoint.HandleAsync).RequireAuthorization();
        return app;
    }
}

public sealed record OpenAccountRequest(string Type, string Currency);

internal static class OpenAccountEndpoint
{
    internal static async Task<IResult> HandleAsync(OpenAccountRequest request, HttpContext http, AccountsDbContext db,
        ICurrentUser user, IClock clock, IOutbox outbox, IConfiguration configuration, CancellationToken ct)
    {
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrEmpty(key)) return ProblemResults.IdempotencyKeyMissing();
        var errors = Validate(request, user, configuration);
        if (errors.Count != 0) return ProblemResults.Validation(errors);
        var account = new Account
        {
            Id = Ids.New(), OwnerId = user.Id, Type = request.Type, Currency = request.Currency, Status = "Active",
            Version = 1, ContactEmail = user.Email, IdempotencyKey = key, OpenedAt = clock.UtcNow
        };
        db.Accounts.Add(account);
        AppendOpened(db, account);
        outbox.Enqueue(new AccountOpenedMessage(account.Id, account.OwnerId, account.Type, account.Currency, account.ContactEmail, account.OpenedAt), account.Id);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: "23505" })
        {
            return ProblemResults.InvalidState("An account with this type and currency already exists.");
        }
        return Results.Created("/accounts/" + account.Id, AccountResponse.From(account));
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
            Data = EnvelopeSerializer.ToPayload(new Ledger.AccountOpened(account.OwnerId, account.Type, null, account.Currency, account.ContactEmail)),
            Metadata = EnvelopeSerializer.ToPayload(new { CorrelationId = account.Id, CausationId = (Guid?)null,
                OperationId = (Guid?)null, account.IdempotencyKey, account.RequestHash })
        });
    }
}
