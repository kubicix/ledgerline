using System.Diagnostics;
using System.Text.Json;
using Ledgerline.BankSimulator.Infrastructure;
using Ledgerline.BankSimulator.Infrastructure.Entities;
using Ledgerline.BuildingBlocks.Web.Idempotency;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.BankSimulator.Features;

public static class BankIdempotency
{
    public static async Task<IResult> ExecuteAsync(HttpContext http, string kind, BankDbContext db,
        Func<Task<BankOperation>> operation, CancellationToken ct)
    {
        if (!IdempotencyKey.TryRead(http, out var key)) return ProblemResults.IdempotencyKeyMissing();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
        var stored = await db.Operations.FindAsync([key], ct);
        if (stored is not null) return Results.Json(stored.ResponseBody, statusCode: stored.HttpStatus);
        var result = await operation();
        result.IdempotencyKey = key!;
        result.Kind = kind;
        if (result.OccurredAt == default) result.OccurredAt = Timestamp();
        db.Operations.Add(result);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Json(result.ResponseBody, statusCode: result.HttpStatus);
    }

    internal static BankOperation Response(int status, object body, BankAuthorization? authorization = null,
        string? refundId = null, DateTimeOffset occurredAt = default) => new()
    {
        HttpStatus = status, ResponseBody = JsonSerializer.SerializeToElement(body, JsonSerializerOptions.Web),
        AuthorizationId = authorization?.Id, CaptureId = authorization?.CaptureId, RefundId = refundId,
        Reference = authorization?.Reference, AmountMinor = authorization?.AmountMinor, Currency = authorization?.Currency,
        OccurredAt = occurredAt
    };

    internal static DateTimeOffset Timestamp() => new(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);

    internal static BankOperation Problem(HttpContext http, int status, string code, string title) => Response(status, new
    {
        type = "urn:ledgerline:problem:" + code, title, status, detail = title,
        traceId = Activity.Current?.Id ?? http.TraceIdentifier
    });

    internal static Task<BankAuthorization?> LockAuthorizationAsync(BankDbContext db, string id, CancellationToken ct) =>
        db.Authorizations.FromSqlInterpolated($"SELECT * FROM bank_authorizations WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
}
