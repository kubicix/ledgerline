using Ledgerline.BuildingBlocks;
using Ledgerline.Risk.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Risk.Infrastructure;

public sealed partial class RuleCache(IClock clock, ILogger<RuleCache> logger, TimeSpan? ttl = null)
{
    private CacheEntry? current;

    public async Task<IReadOnlyList<RiskRule>> GetAsync(RiskDbContext db, CancellationToken ct)
    {
        var cached = Volatile.Read(ref current);
        if (cached is not null && cached.ExpiresAt > clock.UtcNow) return cached.Rules;
        var entities = await db.Rules.AsNoTracking().Where(x => x.Enabled).ToListAsync(ct);
        var rules = new List<RiskRule>();
        foreach (var entity in entities)
        {
            if (entity.Code == RuleCode.VelocityLimit ? entity.MaxCount is null || entity.WindowSeconds is null : entity.ThresholdMinor is null)
                Misconfigured(logger, entity.Id);
            else rules.Add(entity.ToRule());
        }
        Volatile.Write(ref current, new(rules, clock.UtcNow.Add(ttl ?? TimeSpan.FromSeconds(10))));
        return rules;
    }

    [LoggerMessage(1, LogLevel.Error, "Skipping misconfigured risk rule {RuleId}")]
    private static partial void Misconfigured(ILogger logger, Guid ruleId);
    private sealed record CacheEntry(IReadOnlyList<RiskRule> Rules, DateTimeOffset ExpiresAt);
}
