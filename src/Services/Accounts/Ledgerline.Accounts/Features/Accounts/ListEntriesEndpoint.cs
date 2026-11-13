using System.Buffers.Binary;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Pagination;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Accounts.Features.Accounts;

internal static class ListEntriesEndpoint
{
    internal static async Task<IResult> HandleAsync(Guid id, int? limit, string? cursor, AccountsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var owner = user.Id;
        var admin = user.IsInRole("admin");
        if (!await db.Accounts.AnyAsync(a => a.Id == id && (admin || a.OwnerId == owner), ct)) return ProblemResults.NotFound();
        var size = limit ?? 20;
        if (size is < 1 or > 100 || !TryDecode(cursor, out var before))
            return ProblemResults.Validation(new Dictionary<string, string[]> { ["pagination"] = ["Use limit 1 to 100 and a valid cursor."] });
        var rows = await db.JournalLines.Where(l => l.AccountId == id && l.EventGlobalSeq < before)
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (line, entry) => new
            {
                Sequence = line.EventGlobalSeq,
                Item = new AccountEntryResponse(entry.Id, entry.EntryType, line.Direction, line.AmountMinor,
                    entry.Currency, line.BalanceAfterMinor, entry.PaymentId, entry.PostedAt)
            }).OrderByDescending(x => x.Sequence).Take(size + 1).ToListAsync(ct);
        var next = rows.Count > size ? Encode(rows[size - 1].Sequence) : null;
        return Results.Ok(new Page<AccountEntryResponse>(rows.Take(size).Select(x => x.Item).ToArray(), next));
    }

    private static string Encode(long sequence)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, sequence);
        return WebEncoders.Base64UrlEncode(bytes);
    }

    private static bool TryDecode(string? cursor, out long sequence)
    {
        sequence = long.MaxValue;
        if (cursor is null) return true;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(cursor);
            if (bytes.Length != 8) return false;
            sequence = BinaryPrimitives.ReadInt64BigEndian(bytes);
            return sequence > 0;
        }
        catch (FormatException) { return false; }
    }
}

public sealed record AccountEntryResponse(Guid JournalEntryId, string EntryType, string Direction, long AmountMinor,
    string Currency, long BalanceAfterMinor, Guid PaymentId, DateTimeOffset PostedAt);
