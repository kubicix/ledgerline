namespace Ledgerline.Risk.Domain;

public static class RiskEvaluator
{
    public static RiskOutcome Evaluate(IReadOnlyList<RiskRule> rules, RiskRequest request, RiskCounters counters)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(counters);
        var failures = new List<string>();
        foreach (var rule in rules.Where(rule => rule.Enabled).OrderBy(rule => rule.Code))
        {
            if (rule.Currency is not null && rule.Currency != request.Currency) continue;
            if (rule.Kind is not null && rule.Kind != request.Kind) continue;
            var failed = rule.Code switch
            {
                RuleCode.MaxSingleAmount => rule.ThresholdMinor is { } threshold && request.AmountMinor > threshold,
                RuleCode.DailyAmountLimit => rule.ThresholdMinor is { } threshold &&
                    checked(counters.DailyTotalMinor + request.AmountMinor) > threshold,
                RuleCode.VelocityLimit => rule.MaxCount is { } max && rule.WindowSeconds is not null && counters.VelocityCount > max,
                _ => false
            };
            if (failed) failures.Add(rule.Code.ToString());
        }
        return new(failures.Count == 0, failures);
    }
}
