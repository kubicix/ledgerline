using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Risk.Domain;
using Ledgerline.Risk.Infrastructure;

namespace Ledgerline.Risk.Features.Rules;

public sealed record UpdateRuleRequest(long? ThresholdMinor, int? MaxCount, int? WindowSeconds, bool Enabled);

public static class UpdateRuleEndpoint
{
    public static async Task<IResult> HandleAsync(Guid id, UpdateRuleRequest request, RiskDbContext db, IClock clock, CancellationToken ct)
    {
        var rule = await db.Rules.FindAsync([id], ct);
        if (rule is null) return ProblemResults.NotFound();
        var valid = rule.Code == RuleCode.VelocityLimit
            ? request.ThresholdMinor is null && request.MaxCount is > 0 && request.WindowSeconds is > 0
            : request.ThresholdMinor is > 0 && request.MaxCount is null && request.WindowSeconds is null;
        if (!valid) return ProblemResults.Validation(new Dictionary<string, string[]>
        {
            ["rule"] = ["Supply positive parameters for this rule code and leave all other parameters null."]
        });
        rule.ThresholdMinor = request.ThresholdMinor;
        rule.MaxCount = request.MaxCount;
        rule.WindowSeconds = request.WindowSeconds;
        rule.Enabled = request.Enabled;
        rule.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(RuleResponse.From(rule));
    }
}
