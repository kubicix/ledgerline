using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

[assembly: AssemblyFixture(typeof(InfrastructureFixture))]

namespace Ledgerline.Accounts.IntegrationTests;

public sealed class MigrationTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Runtime_owner_can_append_but_cannot_update_or_delete_events()
    {
        var role = "app_" + Guid.NewGuid().ToString("N");
        var database = "grants_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(infrastructure.PostgresAdminConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var createRole = new NpgsqlCommand($"CREATE ROLE \"{role}\" LOGIN PASSWORD 'synthetic_test_only'", admin))
            await createRole.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using (var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{database}\" OWNER \"{role}\"", admin))
            await createDatabase.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var cs = new NpgsqlConnectionStringBuilder(infrastructure.PostgresAdminConnectionString)
        {
            Database = database, Username = role, Password = "synthetic_test_only", Pooling = false
        }.ConnectionString;
        await MigrateCommand.RunAsync(cs, TestContext.Current.CancellationToken);
        await MigrateCommand.RunAsync(cs, TestContext.Current.CancellationToken);
        await using var runtime = new NpgsqlConnection(cs);
        await runtime.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var sql in new[] { "UPDATE events SET type=type WHERE false", "DELETE FROM events WHERE false", "TRUNCATE events" })
        {
            await using var mutation = new NpgsqlCommand(sql, runtime);
            var error = await Should.ThrowAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            error.SqlState.ShouldBe("42501");
        }
        await using var append = new NpgsqlCommand("""
            INSERT INTO events(stream_id,version,type,data,metadata,occurred_at)
            VALUES (@id,1,'synthetic.v1','{}','{}',now()); SELECT count(*) FROM events
            """, runtime);
        append.Parameters.AddWithValue("id", Guid.NewGuid());
        ((long)(await append.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(7);
    }

    [Fact]
    public async Task Migration_creates_all_tables_from_spec()
    {
        var cs = await CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var expected = new Dictionary<string, string>
        {
            ["events"] = "global_seq stream_id version type data metadata occurred_at",
            ["accounts"] = "id owner_id type system_code currency status balance_minor version contact_email idempotency_key request_hash opened_at",
            ["journal_entries"] = "id entry_type operation_id payment_id currency posted_at",
            ["journal_lines"] = "journal_entry_id line_no account_id direction amount_minor balance_after_minor event_global_seq",
            ["holds"] = "payment_id account_id amount_minor currency status",
            ["ledger_operations"] = "operation_id kind payment_id outcome reason_code journal_entry_id completed_at",
            ["outbox_messages"] = "sequence message_id type payload correlation_id causation_id trace_parent transport occurred_at processed_at attempts next_attempt_at last_error",
            ["inbox_messages"] = "message_id consumer type received_at"
        };
        foreach (var (table, columns) in expected)
        {
            await using var command = new NpgsqlCommand("SELECT column_name FROM information_schema.columns WHERE table_schema='public' AND table_name=@table", connection);
            command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            var actual = new List<string>();
            while (await reader.ReadAsync(TestContext.Current.CancellationToken)) actual.Add(reader.GetString(0));
            actual.Order().ShouldBe(columns.Split(' ').Order());
        }
    }

    [Fact]
    public async Task Migration_seeds_system_accounts_for_each_configured_currency()
    {
        await using var db = OpenDb(await CreateDatabaseAsync());
        var accounts = await db.Accounts.ToListAsync(TestContext.Current.CancellationToken);
        accounts.Count.ShouldBe(6);
        foreach (var currency in new[] { "TRY", "EUR", "USD" })
        foreach (var code in new[] { "BANK_CLEARING", "PAYMENTS_PENDING" })
        {
            var account = accounts.Single(a => a.Currency == currency && a.SystemCode == code);
            account.Id.ShouldBe(SystemAccounts.IdFor(code, currency));
            account.Type.ShouldBe("System");
            account.BalanceMinor.ShouldBe(0);
            account.OwnerId.ShouldBeNull();
            var opened = await db.Events.SingleAsync(e => e.StreamId == account.Id, TestContext.Current.CancellationToken);
            opened.Type.ShouldBe("account-opened.v1");
            opened.Version.ShouldBe(1);
            opened.Data.GetProperty("systemCode").GetString().ShouldBe(code);
            opened.Data.GetProperty("currency").GetString().ShouldBe(currency);
        }
    }

    [Fact]
    public async Task Migrate_is_idempotent()
    {
        var cs = await infrastructure.CreateDatabaseAsync("idem_migrate");
        await MigrateCommand.RunAsync(cs, TestContext.Current.CancellationToken);
        await MigrateCommand.RunAsync(cs, TestContext.Current.CancellationToken);
        await using var db = OpenDb(cs);
        (await db.Accounts.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(6);
        (await db.Events.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(6);
        (await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken))
            .ShouldBe(db.Database.GetMigrations());
    }

    [Fact]
    public async Task Wallet_check_constraint_rejects_negative_balance()
    {
        var cs = await CreateDatabaseAsync();
        var id = Guid.NewGuid();
        await using (var db = OpenDb(cs))
        {
            db.Accounts.Add(new Account { Id = id, OwnerId = Guid.NewGuid(), Type = "CustomerWallet", Currency = "TRY", Status = "Active", Version = 1, OpenedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("UPDATE accounts SET balance_minor=-1 WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("ck_wallet_non_negative");
    }

    [Fact]
    public void SystemAccounts_IdFor_is_stable() =>
        SystemAccounts.IdFor("BANK_CLEARING", "TRY").ShouldBe(Guid.Parse("762a998e-0eaa-5b60-b46e-e42d8ed9e3aa"));

    [Fact]
    public async Task Service_host_uses_migrated_database()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "service_host");
        using var client = host.CreateClient(Guid.NewGuid());
        using var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        db.Database.GetDbConnection().Database.ShouldBe(new NpgsqlConnectionStringBuilder(host.ConnectionString).Database);
        (await db.Accounts.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(6);
    }

    [Fact]
    public async Task Disposed_service_host_releases_database_connections()
    {
        var host = await ServiceHost<Program>.StartAsync(infrastructure, "pool_lifetime");
        var database = new NpgsqlConnectionStringBuilder(host.ConnectionString).Database!;
        var dataSource = host.Services.GetRequiredService<NpgsqlDataSource>();
        try
        {
            await Eventually.ShouldAsync(async () => await ConnectionsAsync(database) > 0);
        }
        finally { await host.DisposeAsync(); }
        await Should.ThrowAsync<ObjectDisposedException>(async () => await dataSource.OpenConnectionAsync());
        await Eventually.ShouldAsync(async () => await ConnectionsAsync(database) == 0);
    }

    private async Task<long> ConnectionsAsync(string database)
    {
        await using var connection = new NpgsqlConnection(infrastructure.PostgresAdminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=@database", connection);
        command.Parameters.AddWithValue("database", database);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private Task<string> CreateDatabaseAsync() => DatabaseFactory.CreateMigratedAsync<AccountsDbContext>(
        infrastructure, "accounts", cs => MigrateCommand.RunAsync(cs, TestContext.Current.CancellationToken));
    private static AccountsDbContext OpenDb(string cs) => new(new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(cs).Options);
}
