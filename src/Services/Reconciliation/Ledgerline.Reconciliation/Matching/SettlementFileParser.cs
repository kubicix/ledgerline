using System.Globalization;

namespace Ledgerline.Reconciliation.Matching;

/// <summary>The settlement file is malformed. <see cref="LineNumber"/> is 1-based and counts the header as line 1.</summary>
public sealed class SettlementFormatException : Exception
{
    public SettlementFormatException(int lineNumber, string problem)
        : base($"Settlement file line {lineNumber}: {problem}")
    {
        LineNumber = lineNumber;
    }

    public int LineNumber { get; }
}

/// <summary>
/// Parses the bank's settlement CSV. It accepts exactly what the bank writes: a fixed header, LF or CRLF line endings, no
/// quoting, digits-only amounts and an ISO 8601 UTC timestamp. Amounts are never read as floating point.
/// </summary>
public static class SettlementFileParser
{
    private const string Header = "record_type,bank_id,reference,amount_minor,currency,occurred_at";
    private const int FieldCount = 6;
    private static readonly string[] TimestampFormats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];

    public static IReadOnlyList<BankRecord> Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var lines = csv.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines[0] != Header) throw new SettlementFormatException(1, $"expected the header '{Header}'.");

        var records = new List<BankRecord>(lines.Count - 1);
        for (var index = 1; index < lines.Count; index++) records.Add(ParseLine(lines[index], index + 1));
        return records;
    }

    private static BankRecord ParseLine(string line, int lineNumber)
    {
        if (line.Contains('"', StringComparison.Ordinal)) throw new SettlementFormatException(lineNumber, "quoting is not supported.");
        var fields = line.Split(',');
        if (fields.Length != FieldCount) throw new SettlementFormatException(lineNumber, $"expected {FieldCount} fields, found {fields.Length}.");

        return new BankRecord(
            ParseType(fields[0], lineNumber),
            Required(fields[1], "bank_id", lineNumber),
            Required(fields[2], "reference", lineNumber),
            ParseAmount(fields[3], lineNumber),
            ParseCurrency(fields[4], lineNumber),
            ParseTimestamp(fields[5], lineNumber));
    }

    private static BankRecordType ParseType(string value, int lineNumber) => value switch
    {
        "CAPTURE" => BankRecordType.Capture,
        "REFUND" => BankRecordType.Refund,
        _ => throw new SettlementFormatException(lineNumber, $"record_type must be CAPTURE or REFUND, found '{value}'."),
    };

    private static string Required(string value, string column, int lineNumber) =>
        value.Length > 0 ? value : throw new SettlementFormatException(lineNumber, $"{column} is empty.");

    private static long ParseAmount(string value, int lineNumber) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : throw new SettlementFormatException(lineNumber, $"amount_minor must be a whole number of minor units, found '{value}'.");

    private static string ParseCurrency(string value, int lineNumber) =>
        value.Length == 3 && value.All(char.IsAsciiLetterUpper)
            ? value
            : throw new SettlementFormatException(lineNumber, $"currency must be 3 upper case letters A-Z, found '{value}'.");

    private static DateTimeOffset ParseTimestamp(string value, int lineNumber)
    {
        // The offset must be written out as Z or +00:00: a bare local time is not accepted as UTC.
        var explicitUtc = value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal);
        var parsed = DateTimeOffset.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var timestamp);
        return explicitUtc && parsed && timestamp.Offset == TimeSpan.Zero
            ? timestamp
            : throw new SettlementFormatException(lineNumber, $"occurred_at must be an ISO 8601 UTC timestamp ending in Z or +00:00, found '{value}'.");
    }
}
