using System.Globalization;

namespace Ledgerline.Notifications.Templates;

public sealed record TemplateData(string Kind, long AmountMinor, string Currency, string? Reason = null);
public sealed record RenderedTemplate(string Subject, string Body);

public static class TemplateRenderer
{
    public static RenderedTemplate Render(string template, TemplateData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var amount = (data.AmountMinor / 100).ToString("N0", CultureInfo.InvariantCulture) + "." +
            (data.AmountMinor % 100).ToString("D2", CultureInfo.InvariantCulture) + " " + data.Currency;
        var kind = data.Kind switch { "TopUp" => "top-up", "Transfer" => "transfer", _ => "payment" };
        var subject = template switch
        {
            "payment-completed" => $"Your {kind} of {amount} is complete",
            "transfer-received" => $"You received a transfer of {amount}",
            "payment-authorized" => $"Your payment of {amount} is authorized",
            "payment-captured" => $"Your payment of {amount} was captured",
            "merchant-payment-captured" => $"You received a payment of {amount}",
            "payment-voided" => $"Your payment of {amount} was voided",
            "payment-failed" => $"Your {kind} of {amount} failed",
            "payment-refunded" => $"Your refund of {amount} is complete",
            _ => throw new ArgumentException("Unknown notification template.", nameof(template))
        };
        return new(subject, subject + "." + (data.Reason is null ? "" : " Reason: " + data.Reason + "."));
    }
}
