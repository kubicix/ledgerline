using Ledgerline.BuildingBlocks;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Accounts.Infrastructure;

public static class MigrateCommand
{
    public static Task RunAsync(string connectionString, CancellationToken ct = default) =>
        RunAsync(connectionString, ["TRY", "EUR", "USD"], ct);

    public static async Task RunAsync(string connectionString, IReadOnlyList<string> currencies, CancellationToken ct = default)
    {
        await using var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(ct);
        await SystemAccounts.SeedAsync(db, currencies, new SystemClock(), ct);
    }
}
