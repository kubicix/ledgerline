using System.Text.Json;
using Ledgerline.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Sagas;

internal sealed record SagaActionAudit(Guid PaymentId, string Key, string Hash, Guid OwnerId, Guid? OperationId)
{
    internal static Task<int> LockKeyAsync(PaymentsDbContext db, Guid owner, string key, CancellationToken ct)
    {
        var scope = $"http:{owner}:{key}";
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scope}, 0))", ct);
    }

    internal string Serialize() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);

    internal static async Task<SagaActionAudit?> FindAsync(PaymentsDbContext db, Guid owner, string key, CancellationToken ct)
    {
        var ownerText = owner.ToString();
        var row = await db.SagaSteps.FromSqlInterpolated($"""
            SELECT * FROM saga_steps
            WHERE CASE WHEN outcome = 'Started' AND detail IS NOT NULL THEN detail::jsonb ->> 'key' END = {key}
              AND CASE WHEN outcome = 'Started' AND detail IS NOT NULL THEN detail::jsonb ->> 'ownerId' END = {ownerText}
            ORDER BY id LIMIT 1
            """).AsNoTracking().SingleOrDefaultAsync(ct);
        return row?.Detail is null ? null : JsonSerializer.Deserialize<SagaActionAudit>(row.Detail, JsonSerializerOptions.Web);
    }

    internal static async Task<Guid?> CaptureOperationAsync(PaymentsDbContext db, Guid paymentId, CancellationToken ct)
    {
        var detail = await db.SagaSteps.AsNoTracking().Where(x => x.PaymentId == paymentId && x.Step == SagaStep.LedgerCapture && x.Outcome == "Started" && x.Detail != null)
            .OrderByDescending(x => x.Id).Select(x => x.Detail).FirstOrDefaultAsync(ct);
        return detail is null ? null : JsonSerializer.Deserialize<SagaActionAudit>(detail, JsonSerializerOptions.Web)?.OperationId;
    }
}
