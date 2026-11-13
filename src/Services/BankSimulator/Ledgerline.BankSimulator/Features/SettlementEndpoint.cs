using System.Globalization;
using System.Text;
using Ledgerline.BankSimulator.Infrastructure;
using Ledgerline.BankSimulator.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.BankSimulator.Features;

public static class SettlementEndpoint
{
    public static async Task<IResult> HandleAsync(DateOnly date, BankDbContext db, SettlementTampering tampering, CancellationToken ct)
    {
        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var end = start.AddDays(1);
        var operations = await db.Operations.AsNoTracking().Where(x => x.HttpStatus == 200 &&
            (x.Kind == "Capture" || x.Kind == "Refund") && x.OccurredAt >= start && x.OccurredAt < end)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.IdempotencyKey).ToListAsync(ct);
        var csv = new StringBuilder("record_type,bank_id,reference,amount_minor,currency,occurred_at\n");
        var change = tampering.For(date);
        foreach (var operation in operations)
        {
            if (operation.Kind == "Capture" && change is { DropCaptureId: not null } && operation.CaptureId == change.DropCaptureId) continue;
            Append(csv, operation, change);
        }
        return Results.Text(csv.ToString(), "text/csv", Encoding.UTF8);
    }

    private static void Append(StringBuilder csv, BankOperation operation, SettlementTamperingRequest? change)
    {
        var capture = operation.Kind == "Capture";
        var amount = capture && change is { AlterCaptureId: not null } && change.AlterCaptureId == operation.CaptureId
            ? change.AlteredAmountMinor : operation.AmountMinor;
        csv.Append(capture ? "CAPTURE" : "REFUND").Append(',').Append(Escape(capture ? operation.CaptureId : operation.RefundId))
            .Append(',').Append(Escape(operation.Reference)).Append(',').Append(amount?.ToString(CultureInfo.InvariantCulture))
            .Append(',').Append(Escape(operation.Currency)).Append(',').Append(operation.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Escape(string? value) => value is null ? "" :
        value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}
