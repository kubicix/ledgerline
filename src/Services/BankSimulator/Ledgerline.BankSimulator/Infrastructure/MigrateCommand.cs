using Microsoft.EntityFrameworkCore;

namespace Ledgerline.BankSimulator.Infrastructure;

public static class MigrateCommand
{
    public static async Task RunAsync(string connectionString, CancellationToken ct = default)
    {
        await using var db = new BankDbContext(new DbContextOptionsBuilder<BankDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(ct);
    }
}
