using Npgsql;

namespace Ledgerline.SystemTests.Harness;

public sealed record PaymentState(Guid Id, string Status, string SagaStep);

public sealed record InboxRow(Guid MessageId, string Consumer);

/// <summary>
/// Read-only SQL on the published Postgres (localhost:5432). Used where the gateway cannot answer, for example while a
/// service is killed, and for facts no API exposes (saga steps, journal entries per payment).
/// </summary>
public static class SystemQueries
{
    /// <summary>The database clock, which the service containers share; use it to bound "created since the run started".</summary>
    public static async Task<DateTimeOffset> DatabaseNowAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(SystemInvariants.PaymentsDatabase, ct);
        await using var command = new NpgsqlCommand("SELECT now()", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    /// <summary>payments_db: status and saga step of each given payment that exists.</summary>
    public static async Task<IReadOnlyList<PaymentState>> PaymentStatesAsync(IEnumerable<Guid> paymentIds, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(SystemInvariants.PaymentsDatabase, ct);
        await using var command = new NpgsqlCommand("SELECT id, status, saga_step FROM payments WHERE id = ANY(@ids)", connection);
        command.Parameters.AddWithValue("ids", paymentIds.ToArray());
        var states = new List<PaymentState>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) states.Add(new PaymentState(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        return states;
    }

    /// <summary>
    /// payments_db: payments created with a harness key (<c>systest-</c>) at or after <paramref name="since"/> that are not
    /// in <paramref name="recorded"/>. A non-empty result means a POST committed a payment the run never saw.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> UnrecordedPaymentsAsync(IEnumerable<Guid> recorded, DateTimeOffset since, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(SystemInvariants.PaymentsDatabase, ct);
        await using var command = new NpgsqlCommand("""
            SELECT id FROM payments WHERE idempotency_key LIKE 'systest-%' AND created_at >= @since AND NOT (id = ANY(@recorded))
            """, connection);
        command.Parameters.AddWithValue("since", since);
        command.Parameters.AddWithValue("recorded", recorded.ToArray());
        return await ReadGuidsAsync(command, ct);
    }

    /// <summary>accounts_db: number of journal entries of <paramref name="entryType"/> per payment, for payments that have any.</summary>
    public static async Task<IReadOnlyDictionary<Guid, int>> JournalEntriesAsync(string entryType, IEnumerable<Guid> paymentIds, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(SystemInvariants.AccountsDatabase, ct);
        await using var command = new NpgsqlCommand("""
            SELECT payment_id, count(*)::int FROM journal_entries WHERE entry_type = @type AND payment_id = ANY(@ids) GROUP BY payment_id
            """, connection);
        command.Parameters.AddWithValue("type", entryType);
        command.Parameters.AddWithValue("ids", paymentIds.ToArray());
        var counts = new Dictionary<Guid, int>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) counts.Add(reader.GetGuid(0), reader.GetInt32(1));
        return counts;
    }

    /// <summary>
    /// accounts_db, whole history: payments with more than one TopUp journal entry, that is a wallet credited twice for one
    /// top-up. <c>operation_id</c> is unique, so the check is per payment: a second credit would carry a different operation.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> PaymentsCreditedTwiceAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(SystemInvariants.AccountsDatabase, ct);
        await using var command = new NpgsqlCommand(
            "SELECT payment_id FROM journal_entries WHERE entry_type = 'TopUp' GROUP BY payment_id HAVING count(*) > 1", connection);
        return await ReadGuidsAsync(command, ct);
    }

    /// <summary>
    /// Inbox rows of the given messages in every service database: one row per message a consumer committed (a duplicate
    /// delivery of the same messageId adds no row).
    /// </summary>
    public static async Task<IReadOnlyList<InboxRow>> InboxRowsAsync(IEnumerable<Guid> messageIds, CancellationToken ct = default)
    {
        var ids = messageIds.ToArray();
        var rows = new List<InboxRow>();
        foreach (var database in InboxDatabases)
        {
            await using var connection = await OpenAsync(database, ct);
            await using var command = new NpgsqlCommand("SELECT message_id, consumer FROM inbox_messages WHERE message_id = ANY(@ids)", connection);
            command.Parameters.AddWithValue("ids", ids);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(new InboxRow(reader.GetGuid(0), reader.GetString(1)));
        }
        return rows;
    }

    /// <summary>Outbox rows not yet published, summed over the services that have an outbox (accounts, payments, risk).</summary>
    public static async Task<long> PendingOutboxMessagesAsync(CancellationToken ct = default)
    {
        long pending = 0;
        foreach (var database in OutboxDatabases)
        {
            await using var connection = await OpenAsync(database, ct);
            await using var command = new NpgsqlCommand("SELECT count(*) FROM outbox_messages WHERE processed_at IS NULL", connection);
            pending += (long)(await command.ExecuteScalarAsync(ct))!;
        }
        return pending;
    }

    // Local development credentials from deploy/compose/postgres/init-databases.sql.
    private const string RiskDatabase = "Host=localhost;Port=5432;Database=risk_db;Username=risk;Password=risk_dev";
    private const string NotificationsDatabase = "Host=localhost;Port=5432;Database=notifications_db;Username=notifications;Password=notifications_dev";
    private static readonly string[] OutboxDatabases = [SystemInvariants.AccountsDatabase, SystemInvariants.PaymentsDatabase, RiskDatabase];
    private static readonly string[] InboxDatabases = [.. OutboxDatabases, NotificationsDatabase];

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<IReadOnlyList<Guid>> ReadGuidsAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids;
    }
}
