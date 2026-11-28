using System.Globalization;
using Ledgerline.Notifications.Templates;

namespace Ledgerline.Notifications.Tests;

public sealed class TemplateTests
{
    [Fact]
    public void Amount_is_rendered_as_major_units_with_currency()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            TemplateRenderer.Render("payment-completed", new("TopUp", 150000, "TRY")).Subject
                .ShouldContain("1,500.00 TRY");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Mismatch_renders_code_reference_amounts_and_run_in_invariant_culture()
    {
        var run = Guid.NewGuid();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var result = TemplateRenderer.RenderMismatch(new(run, new DateOnly(2026, 11, 13), "AmountMismatch", "payment-1", 123456, 123400, "EUR"));
            result.Subject.ShouldBe("Reconciliation mismatch on 2026-11-13: AmountMismatch");
            result.Body.Split('\n').ShouldBe([
                "Reconciliation mismatch on 2026-11-13: AmountMismatch.",
                "Code: AmountMismatch",
                "Reference: payment-1",
                "Bank amount: 1,234.56 EUR",
                "Ledger amount: 1,234.00 EUR",
                "Run: " + run]);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Mismatch_renders_a_missing_amount_as_none_and_a_negative_amount_with_its_sign()
    {
        var result = TemplateRenderer.RenderMismatch(new(Guid.Empty, new DateOnly(2026, 11, 13), "LedgerImbalance", "TRY", null, -5, "TRY"));
        result.Body.ShouldContain("Bank amount: none");
        result.Body.ShouldContain("Ledger amount: -0.05 TRY");
        TemplateRenderer.RenderMismatch(new(Guid.Empty, new DateOnly(2026, 11, 13), "LedgerImbalance", "TRY", null, -123456, "TRY"))
            .Body.ShouldContain("Ledger amount: -1,234.56 TRY");
        TemplateRenderer.RenderMismatch(new(Guid.Empty, new DateOnly(2026, 11, 13), "LedgerImbalance", "TRY", null, long.MinValue, "TRY"))
            .Body.ShouldContain("Ledger amount: -92,233,720,368,547,758.08 TRY");
    }

    [Fact]
    public void Mismatch_without_a_currency_renders_the_amount_alone()
    {
        TemplateRenderer.RenderMismatch(new(Guid.Empty, new DateOnly(2026, 11, 13), "AmountMismatch", "payment-1", 100, null, null))
            .Body.ShouldContain("Bank amount: 1.00" + "\nLedger amount: none");
    }

    [Theory]
    [InlineData("payment-completed")]
    [InlineData("transfer-received")]
    [InlineData("payment-authorized")]
    [InlineData("payment-captured")]
    [InlineData("merchant-payment-captured")]
    [InlineData("payment-voided")]
    [InlineData("payment-failed")]
    [InlineData("payment-refunded")]
    public void Every_template_renders_plain_text(string template)
    {
        var result = TemplateRenderer.Render(template, new("Transfer", 12345, "EUR", "Declined"));
        result.Subject.ShouldContain("123.45 EUR");
        result.Body.ShouldContain("123.45 EUR");
        result.Body.ShouldNotContain("<html");
    }
}
