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
