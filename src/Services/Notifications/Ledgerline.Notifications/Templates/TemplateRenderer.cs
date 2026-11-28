using System.Globalization;

namespace Ledgerline.Notifications.Templates;

public sealed record TemplateData(string Kind, long AmountMinor, string Currency, string? Reason = null);
public sealed record RenderedTemplate(string Subject, string Body);

/// <summary>The fields of one <c>reconciliation-mismatch-detected</c> event that the <c>reconciliation-mismatch</c> template shows.</summary>
public sealed record MismatchData(Guid RunId, DateOnly BusinessDate, string Code, string Reference,
    long? BankAmountMinor, long? LedgerAmountMinor, string? Currency);

public static class TemplateRenderer
{
    public const string MismatchTemplate = "reconciliation-mismatch";

    /// <summary>
    /// The <c>reconciliation-mismatch</c> template. Amounts are major units in invariant culture followed by the currency
    /// code, a missing amount is <c>none</c>, and a ledger imbalance may be negative, so the sign is written on its own.
    /// </summary>
    public static RenderedTemplate RenderMismatch(MismatchData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var subject = "Reconciliation mismatch on " + data.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ": " + data.Code;
        var body = string.Join('\n',
            subject + ".",
            "Code: " + data.Code,
            "Reference: " + data.Reference,
            "Bank amount: " + Amount(data.BankAmountMinor, data.Currency),
            "Ledger amount: " + Amount(data.LedgerAmountMinor, data.Currency),
            "Run: " + data.RunId.ToString("D", CultureInfo.InvariantCulture));
        return new(subject, body);
    }

    private static string Amount(long? minor, string? currency)
    {
        if (minor is not { } value) return "none";
        // The magnitude as unsigned, so long.MinValue needs no negation.
        var magnitude = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
        var text = (value < 0 ? "-" : "") + (magnitude / 100).ToString("N0", CultureInfo.InvariantCulture) + "." +
            (magnitude % 100).ToString("D2", CultureInfo.InvariantCulture);
        return currency is null ? text : text + " " + currency;
    }

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
