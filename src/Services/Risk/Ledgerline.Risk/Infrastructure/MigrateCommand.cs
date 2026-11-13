using Ledgerline.Risk.Domain;
using Ledgerline.Risk.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Risk.Infrastructure;

public static class MigrateCommand
{
    public static async Task RunAsync(string connectionString, CancellationToken ct = default)
    {
        await using var db = new RiskDbContext(new DbContextOptionsBuilder<RiskDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(ct);
        foreach (var currency in new[] { "TRY", "EUR", "USD" })
        {
            await SeedAsync(db, RuleCode.MaxSingleAmount, currency, 5_000_000, ct);
            await SeedAsync(db, RuleCode.DailyAmountLimit, currency, 20_000_000, ct);
        }
        await SeedAsync(db, RuleCode.VelocityLimit, null, null, ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedAsync(RiskDbContext db, RuleCode code, string? currency, long? threshold, CancellationToken ct)
    {
        if (await db.Rules.AnyAsync(x => x.Code == code && x.Currency == currency, ct)) return;
        db.Rules.Add(new RiskRuleEntity
        {
            Id = Guid.NewGuid(), Code = code, Currency = currency, ThresholdMinor = threshold,
            MaxCount = code == RuleCode.VelocityLimit ? 10 : null,
            WindowSeconds = code == RuleCode.VelocityLimit ? 60 : null, Enabled = true, UpdatedAt = DateTimeOffset.UtcNow
        });
    }
}
