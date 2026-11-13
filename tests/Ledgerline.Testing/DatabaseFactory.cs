using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledgerline.Testing;

public static class DatabaseFactory
{
    private static readonly ConcurrentDictionary<(string Admin, Type Context), Lazy<Task<string>>> Templates = new();

    public static Task<string> CreateMigratedAsync<TDbContext>(InfrastructureFixture infrastructure, string prefix,
        Func<string, Task> migrate) where TDbContext : DbContext => CreateMigratedAsync(infrastructure, prefix, typeof(TDbContext), migrate);

    internal static async Task<string> CreateMigratedAsync(InfrastructureFixture infrastructure, string prefix,
        Type contextType, Func<string, Task> migrate)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentNullException.ThrowIfNull(migrate);
        if (string.IsNullOrEmpty(prefix) || prefix.Length > 20 || prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Database prefix must contain at most 20 ASCII letters, digits or underscores.", nameof(prefix));
        var template = Templates.GetOrAdd((infrastructure.PostgresAdminConnectionString, contextType),
            _ => new Lazy<Task<string>>(() => BuildTemplateAsync(infrastructure, prefix, migrate)));
        var templateConnection = new NpgsqlConnectionStringBuilder(await template.Value);
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(infrastructure.PostgresAdminConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE \"{templateConnection.Database}\"", admin);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(infrastructure.PostgresAdminConnectionString) { Database = name }.ConnectionString;
    }

    private static async Task<string> BuildTemplateAsync(InfrastructureFixture infrastructure, string prefix, Func<string, Task> migrate)
    {
        var cs = await infrastructure.CreateDatabaseAsync(prefix[..Math.Min(prefix.Length, 16)] + "tpl");
        var unpooled = new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString;
        await migrate(unpooled);
        return unpooled;
    }
}
