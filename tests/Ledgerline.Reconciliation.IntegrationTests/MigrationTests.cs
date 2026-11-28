using System.Security.Cryptography;
using System.Text;
using Ledgerline.Reconciliation.Infrastructure;
using Ledgerline.Reconciliation.Infrastructure.Migrations;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

[assembly: AssemblyFixture(typeof(InfrastructureFixture))]

namespace Ledgerline.Reconciliation.IntegrationTests;

public sealed class MigrationTests(InfrastructureFixture infrastructure)
{
    private static readonly string[] QuartzTableNames =
    [
        "qrtz_job_details", "qrtz_triggers", "qrtz_simple_triggers", "qrtz_simprop_triggers", "qrtz_cron_triggers",
        "qrtz_blob_triggers", "qrtz_calendars", "qrtz_paused_trigger_grps", "qrtz_paused_job_grps", "qrtz_fired_triggers",
        "qrtz_scheduler_state", "qrtz_locks", "qrtz_execution_history", "qrtz_misfire_history", "qrtz_job_status"
    ];

    [Fact]
    public async Task Migration_creates_all_tables_from_spec()
    {
        await using var connection = await OpenAsync(await CreateDatabaseAsync());
        // reconciliation.md section 6, column by column: name, type, NOT NULL, default.
        var expected = new Dictionary<string, string[]>
        {
            ["staged_payments"] =
            [
                "payment_id uuid not null", "kind text not null", "status text not null", "amount_minor int8 not null",
                "currency bpchar(3) not null", "bank_capture_id text", "occurred_at timestamptz not null", "last_message_id uuid not null"
            ],
            ["staged_ledger_lines"] =
            [
                "journal_entry_id uuid not null", "line_no int4 not null", "account_id uuid not null", "is_bank_clearing bool not null",
                "entry_type text not null", "direction text not null", "amount_minor int8 not null", "currency bpchar(3) not null",
                "payment_id uuid not null", "posted_at timestamptz not null"
            ],
            ["reconciliation_runs"] =
            [
                "id uuid not null", "business_date date not null", "status text not null", "matched int4 not null default 0",
                "mismatched int4 not null default 0", "inputs_incomplete bool not null default false", "error text",
                "started_at timestamptz not null", "completed_at timestamptz"
            ],
            ["reconciliation_items"] =
            [
                "id uuid not null", "run_id uuid not null", "code text not null", "reference text not null", "bank_amount_minor int8",
                "ledger_amount_minor int8", "currency bpchar(3)"
            ]
        };
        foreach (var (table, columns) in expected)
            (await ColumnsAsync(connection, table)).ShouldBe(columns, ignoreOrder: true, customMessage: table);
        (await ColumnsAsync(connection, "outbox_messages")).Select(x => x.Split(' ')[0]).ShouldBe(
            "sequence message_id type payload correlation_id causation_id trace_parent transport occurred_at processed_at attempts next_attempt_at last_error".Split(' '),
            ignoreOrder: true);
        (await PrimaryKeyAsync(connection, "staged_payments")).ShouldBe(["payment_id"]);
        (await PrimaryKeyAsync(connection, "staged_ledger_lines")).ShouldBe(["journal_entry_id", "line_no"]);
        (await ColumnsAsync(connection, "inbox_messages")).Select(x => x.Split(' ')[0]).ShouldBe(
            "message_id consumer type received_at".Split(' '), ignoreOrder: true);
        // Both messaging tables, as in every service database that uses messaging (building-blocks.md section 6).
        (await TablesAsync(connection)).ShouldBe(
            ["__EFMigrationsHistory", "staged_payments", "staged_ledger_lines", "reconciliation_runs", "reconciliation_items",
                "outbox_messages", "inbox_messages", .. QuartzTableNames],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Business_date_is_unique()
    {
        await using var connection = await OpenAsync(await CreateDatabaseAsync());
        await InsertRunAsync(connection, Guid.NewGuid(), new DateOnly(2026, 11, 27));
        var error = await Should.ThrowAsync<PostgresException>(() => InsertRunAsync(connection, Guid.NewGuid(), new DateOnly(2026, 11, 27)));
        error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Deleting_a_run_deletes_its_items()
    {
        await using var connection = await OpenAsync(await CreateDatabaseAsync());
        var run = Guid.NewGuid();
        var other = Guid.NewGuid();
        await InsertRunAsync(connection, run, new DateOnly(2026, 11, 26));
        await InsertRunAsync(connection, other, new DateOnly(2026, 11, 27));
        (await ScalarAsync(connection, $"SELECT concat_ws(' ', matched, mismatched, inputs_incomplete) FROM reconciliation_runs WHERE id = '{run}'"))
            .ShouldBe("0 0 f");
        foreach (var owner in new[] { run, run, other })
            await ExecuteAsync(connection, $"""
                INSERT INTO reconciliation_items (id, run_id, code, reference, bank_amount_minor, ledger_amount_minor, currency)
                VALUES ('{Guid.NewGuid()}', '{owner}', 'AMOUNT_MISMATCH', 'cap-1', 100, 90, 'TRY')
                """);
        await ExecuteAsync(connection, $"DELETE FROM reconciliation_runs WHERE id = '{run}'");
        (await ScalarAsync(connection, "SELECT count(*) FROM reconciliation_items")).ShouldBe(1L);
        (await ScalarAsync(connection, $"SELECT count(*) FROM reconciliation_items WHERE run_id = '{other}'")).ShouldBe(1L);
    }

    [Fact]
    public async Task Migrate_is_idempotent()
    {
        var cs = await infrastructure.CreateDatabaseAsync("recon_idem");
        await MigrateCommand.RunAsync(cs, TestContext.Current.CancellationToken);
        await MigrateCommand.RunAsync(cs, TestContext.Current.CancellationToken);
        await using var db = OpenDb(cs);
        (await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).ShouldBe(db.Database.GetMigrations());
    }

    [Fact]
    public async Task Migration_down_removes_every_table()
    {
        var cs = await CreateDatabaseAsync();
        await using (var db = OpenDb(cs))
            await db.GetService<IMigrator>().MigrateAsync("0", cancellationToken: TestContext.Current.CancellationToken);
        await using var connection = await OpenAsync(cs);
        (await TablesAsync(connection)).ShouldBe(["__EFMigrationsHistory"]);
    }

    [Fact]
    public void Quartz_script_is_the_official_v4_7_3_script()
    {
        // The repository's line ending conversion may give the working copy CRLF; the committed bytes are LF.
        var bytes = Encoding.UTF8.GetBytes(QuartzTables.Script.Replace("\r\n", "\n", StringComparison.Ordinal));
        Convert.ToHexStringLower(SHA256.HashData(bytes)).ShouldBe("2cfd61fd706512a310a1cd3758b2f029602d4551a2d64c78ee5a8fb45426ceb8");
    }

    private Task<string> CreateDatabaseAsync() => DatabaseFactory.CreateMigratedAsync<ReconciliationDbContext>(
        infrastructure, "recon", cs => MigrateCommand.RunAsync(cs, TestContext.Current.CancellationToken));

    private static ReconciliationDbContext OpenDb(string cs) =>
        new(new DbContextOptionsBuilder<ReconciliationDbContext>().UseNpgsql(cs).Options);

    private static async Task<NpgsqlConnection> OpenAsync(string cs)
    {
        var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static Task InsertRunAsync(NpgsqlConnection connection, Guid id, DateOnly businessDate) => ExecuteAsync(connection, $"""
        INSERT INTO reconciliation_runs (id, business_date, status, started_at) VALUES ('{id}', '{businessDate:yyyy-MM-dd}', 'Running', now())
        """);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> ColumnsAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand("""
            SELECT column_name, udt_name, character_maximum_length, is_nullable, column_default
            FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table
            """, connection);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var column = new StringBuilder(reader.GetString(0)).Append(' ').Append(reader.GetString(1));
            if (!reader.IsDBNull(2)) column.Append('(').Append(reader.GetInt32(2)).Append(')');
            if (reader.GetString(3) == "NO") column.Append(" not null");
            if (!reader.IsDBNull(4)) column.Append(" default ").Append(reader.GetString(4));
            columns.Add(column.ToString());
        }
        return columns;
    }

    private static async Task<List<string>> PrimaryKeyAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand("""
            SELECT a.attname FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
            WHERE i.indrelid = @table::regclass AND i.indisprimary
            ORDER BY array_position(i.indkey::int2[], a.attnum)
            """, connection);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<List<string>> TablesAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var tables = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) tables.Add(reader.GetString(0));
        return tables;
    }
}
