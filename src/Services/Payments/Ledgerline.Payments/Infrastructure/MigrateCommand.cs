using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Infrastructure;

public static class MigrateCommand
{
    public static async Task RunAsync(string connectionString, CancellationToken ct = default)
    {
        await using var db = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(ct);
    }
}
