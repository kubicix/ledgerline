using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Notifications.Infrastructure;

public static class MigrateCommand
{
    public static async Task RunAsync(string connectionString, CancellationToken ct = default)
    {
        await using var db = new NotificationsDbContext(new DbContextOptionsBuilder<NotificationsDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(ct);
    }
}
