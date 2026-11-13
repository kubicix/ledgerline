using Ledgerline.Risk.Domain;

namespace Ledgerline.Risk.Tests;

public sealed class RiskEvaluatorTests
{
    private static readonly RiskRequest Request = new(Guid.NewGuid(), "TopUp", Guid.NewGuid(), Guid.NewGuid(),
        100, "TRY", DateTimeOffset.Parse("2026-11-13T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Approves_when_no_rules_enabled()
    {
        var result = RiskEvaluator.Evaluate([], Request, new(0, 0));
        result.Approved.ShouldBeTrue();
        result.RuleCodes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void MaxSingleAmount_rejects_above_threshold_and_allows_equal(long amount, bool approved) =>
        RiskEvaluator.Evaluate([Rule(RuleCode.MaxSingleAmount)], Request with { AmountMinor = amount }, new(0, 1))
            .Approved.ShouldBe(approved);

    [Theory]
    [InlineData("TopUp", false)]
    [InlineData("Transfer", true)]
    public void MaxSingleAmount_with_kind_applies_only_to_that_kind(string kind, bool approved) =>
        RiskEvaluator.Evaluate([Rule(RuleCode.MaxSingleAmount) with { Kind = "TopUp" }],
            Request with { Kind = kind, AmountMinor = 101 }, new(0, 1)).Approved.ShouldBe(approved);

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void DailyAmountLimit_rejects_when_total_plus_amount_exceeds(long total, bool approved) =>
        RiskEvaluator.Evaluate([Rule(RuleCode.DailyAmountLimit)], Request, new(total, 1)).Approved.ShouldBe(approved);

    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void VelocityLimit_rejects_when_count_exceeds_max(int count, bool approved) =>
        RiskEvaluator.Evaluate([Rule(RuleCode.VelocityLimit)], Request, new(0, count)).Approved.ShouldBe(approved);

    [Fact]
    public void Reports_all_failing_rules()
    {
        var result = RiskEvaluator.Evaluate([Rule(RuleCode.VelocityLimit), Rule(RuleCode.DailyAmountLimit), Rule(RuleCode.MaxSingleAmount)],
            Request with { AmountMinor = 101 }, new(100, 11));
        result.Approved.ShouldBeFalse();
        result.RuleCodes.ShouldBe(["MaxSingleAmount", "DailyAmountLimit", "VelocityLimit"]);
    }

    [Fact]
    public void Rule_for_other_currency_is_ignored() =>
        RiskEvaluator.Evaluate([Rule(RuleCode.MaxSingleAmount) with { Currency = "EUR" }],
            Request with { AmountMinor = 101 }, new(0, 1)).Approved.ShouldBeTrue();

    [Fact]
    public void Disabled_rule_is_skipped() =>
        RiskEvaluator.Evaluate([Rule(RuleCode.MaxSingleAmount) with { Enabled = false }],
            Request with { AmountMinor = 101 }, new(0, 1)).Approved.ShouldBeTrue();

    private static RiskRule Rule(RuleCode code) => new(Guid.NewGuid(), code, null,
        code == RuleCode.VelocityLimit ? null : "TRY", code == RuleCode.VelocityLimit ? null : 100,
        code == RuleCode.VelocityLimit ? 10 : null, code == RuleCode.VelocityLimit ? 60 : null, true);
}
