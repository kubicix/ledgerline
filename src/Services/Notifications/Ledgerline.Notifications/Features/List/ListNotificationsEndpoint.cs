using System.Text.Json;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Pagination;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Notifications.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Notifications.Features.List;

internal static class ListNotificationsEndpoint
{
    internal static void MapNotifications(this WebApplication app) =>
        app.MapGet("/notifications", HandleAsync).RequireAuthorization();

    private static async Task<IResult> HandleAsync(int? limit, string? cursor, NotificationsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var size = limit ?? 20;
        if (size is < 1 or > 100 || !Cursor.TryDecode(cursor, out var before))
            return ProblemResults.Validation(new Dictionary<string, string[]> { ["query"] = ["Use limit 1 to 100 and a valid cursor."] });
        var query = db.Notifications.AsNoTracking().Where(x => x.RecipientOwnerId == user.Id);
        if (before is not null) query = query.Where(x => x.CreatedAt < before.At || x.CreatedAt == before.At && x.Id.CompareTo(before.Id) < 0);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(size + 1).ToListAsync(ct);
        var next = rows.Count > size ? new Cursor(rows[size - 1].CreatedAt, rows[size - 1].Id).Encode() : null;
        var items = rows.Take(size).Select(x => new NotificationResponse(x.Id, x.Template, x.PaymentId, x.Status, x.Subject, x.CreatedAt, x.SentAt)).ToArray();
        return Results.Ok(new Page<NotificationResponse>(items, next));
    }

    private sealed record Cursor(DateTimeOffset At, Guid Id)
    {
        internal string Encode() => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(this));
        internal static bool TryDecode(string? value, out Cursor? cursor)
        {
            cursor = null;
            if (value is null) return true;
            try
            {
                cursor = JsonSerializer.Deserialize<Cursor>(WebEncoders.Base64UrlDecode(value));
                return cursor is not null && cursor.Id != Guid.Empty && cursor.At.Offset == TimeSpan.Zero;
            }
            catch (FormatException) { return false; }
            catch (JsonException) { return false; }
        }
    }
    private sealed record NotificationResponse(Guid Id, string Template, Guid? PaymentId, string Status, string Subject, DateTimeOffset CreatedAt, DateTimeOffset? SentAt);
}
