using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.Risk.Infrastructure;
using Ledgerline.Risk.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Risk.Features.Rules;

public static class ListRulesEndpoint
{
    public static void MapRiskEndpoints(this WebApplication app)
    {
        app.MapGet("/risk/rules", async (RiskDbContext db, CancellationToken ct) =>
            Results.Ok(new { items = (await db.Rules.AsNoTracking().OrderBy(x => x.Code).ThenBy(x => x.Currency).ToListAsync(ct)).Select(RuleResponse.From) }))
            .RequireAuthorization(Policies.Admin);
        app.MapPut("/risk/rules/{id:guid}", UpdateRuleEndpoint.HandleAsync).RequireAuthorization(Policies.Admin);
        app.MapGet("/risk/decisions/{paymentId:guid}", GetDecisionEndpoint.HandleAsync).RequireAuthorization(Policies.Admin);
    }
}

public sealed record RuleResponse(Guid Id, string Code, string? Kind, string? Currency, long? ThresholdMinor,
    int? MaxCount, int? WindowSeconds, bool Enabled)
{
    internal static RuleResponse From(RiskRuleEntity rule) => new(rule.Id, rule.Code.ToString(), rule.Kind,
        rule.Currency, rule.ThresholdMinor, rule.MaxCount, rule.WindowSeconds, rule.Enabled);
}
