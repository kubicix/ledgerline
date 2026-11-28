using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Reconciliation.Infrastructure;

public static class MigrateCommand
{
    public static async Task RunAsync(string connectionString, CancellationToken ct = default)
    {
        await using var db = new ReconciliationDbContext(new DbContextOptionsBuilder<ReconciliationDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(ct);
    }
}
