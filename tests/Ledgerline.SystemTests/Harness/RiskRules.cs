namespace Ledgerline.SystemTests.Harness;

public sealed record RiskRuleDto(Guid Id, string Code, string? Kind, string? Currency, long? ThresholdMinor, int? MaxCount, int? WindowSeconds, bool Enabled);

/// <summary>
/// Risk's admin API (<c>GET</c> and <c>PUT /api/risk/rules</c>). The seeded velocity rule allows 10 payments per customer
/// per 60 s, and the seeded realm has two customers, so failure tests with 100 or more payments would end mostly Rejected
/// and never reach the bank or the ledger. <see cref="ComposeStack.EnsureVelocityRaisedAsync"/> raises the rule once per
/// test process and restores it when the collection ends.
/// </summary>
public static class RiskRules
{
    public const int RaisedVelocityMaxCount = 100_000;

    // The velocity rule as Risk's MigrateCommand seeds it.
    public const int SeededVelocityMaxCount = 10;
    public const int SeededVelocityWindowSeconds = 60;

    public static async Task<RiskRuleDto> GetVelocityRuleAsync(LedgerlineClient client, CancellationToken ct = default)
    {
        var rules = await client.GetAsync<RuleList>(KeycloakTokens.Admin, "/api/risk/rules", ct);
        return rules.Items.Single(x => x.Code == "VelocityLimit");
    }

    /// <summary>
    /// The values a restore must put back. A rule that already carries the raised MaxCount was left behind by a run that
    /// died before its restore, so the seeded rule is restored instead of that stale value.
    /// </summary>
    public static RiskRuleDto RestoreTarget(RiskRuleDto current) => current.MaxCount == RaisedVelocityMaxCount
        ? current with { ThresholdMinor = null, MaxCount = SeededVelocityMaxCount, WindowSeconds = SeededVelocityWindowSeconds, Enabled = true }
        : current;

    public static Task<RiskRuleDto> UpdateAsync(LedgerlineClient client, RiskRuleDto rule, CancellationToken ct = default) =>
        client.PutAsync<RiskRuleDto>(KeycloakTokens.Admin, $"/api/risk/rules/{rule.Id}",
            new { rule.ThresholdMinor, rule.MaxCount, rule.WindowSeconds, rule.Enabled }, ct);

    private sealed record RuleList(IReadOnlyList<RiskRuleDto> Items);
}
