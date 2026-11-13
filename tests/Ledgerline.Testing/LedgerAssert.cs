using System.Text.Json;
using Npgsql;

namespace Ledgerline.Testing;

public static class LedgerAssert
{
    public static async Task InvariantsHoldAsync(string accountsConnectionString)
    {
        await using var connection = new NpgsqlConnection(accountsConnectionString);
        await connection.OpenAsync();
        await AssertZeroAsync(connection, "SELECT count(*) FROM (SELECT currency FROM accounts GROUP BY currency HAVING sum(balance_minor) <> 0) bad", "Trial balance must be zero per currency.");
        await AssertZeroAsync(connection, """
            SELECT count(*) FROM (
                SELECT e.id FROM journal_entries e LEFT JOIN journal_lines l ON l.journal_entry_id=e.id
                GROUP BY e.id HAVING count(l.line_no)<2
                OR sum(CASE WHEN l.direction='Credit' THEN l.amount_minor ELSE -l.amount_minor END)<>0
            ) bad
            """, "Every entry must have at least two balanced lines.");
        await AssertZeroAsync(connection, "SELECT count(*) FROM accounts WHERE type <> 'System' AND balance_minor < 0", "Wallets cannot be negative.");
        var accounts = new Dictionary<Guid, AccountState>();
        var lines = new List<LineState>();
        await ReplayAsync(connection, accounts, lines);
        await AssertAccountsAsync(connection, accounts);
        await AssertLinesAsync(connection, lines);
    }

    private static async Task AssertZeroAsync(NpgsqlConnection connection, string sql, string because)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        ((long)(await command.ExecuteScalarAsync())!).ShouldBe(0L, because);
    }

    private static async Task ReplayAsync(NpgsqlConnection connection, Dictionary<Guid, AccountState> accounts, List<LineState> lines)
    {
        await using var command = new NpgsqlCommand("SELECT global_seq,stream_id,version,type,data,occurred_at FROM events ORDER BY global_seq", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var sequence = reader.GetInt64(0);
            var id = reader.GetGuid(1);
            var version = reader.GetInt32(2);
            var type = reader.GetString(3);
            using var document = JsonDocument.Parse(reader.GetString(4));
            var data = document.RootElement;
            if (type == "account-opened.v1")
            {
                version.ShouldBe(1);
                accounts.TryAdd(id, new AccountState(NullableGuid(data, "ownerId"), data.GetProperty("type").GetString()!,
                    NullableString(data, "systemCode"), data.GetProperty("currency").GetString()!, "Active", 0, 1,
                    NullableString(data, "contactEmail"), reader.GetFieldValue<DateTimeOffset>(5))).ShouldBeTrue();
                continue;
            }
            accounts.ContainsKey(id).ShouldBeTrue("A stream must start with account-opened.");
            var previous = accounts[id];
            version.ShouldBe(previous.Version + 1, "Stream versions must be gapless.");
            if (type == "account-status-changed.v1") accounts[id] = previous with { Status = data.GetProperty("status").GetString()!, Version = version };
            else if (type == "ledger-entry-posted.v1") accounts[id] = ApplyPosting(id, version, sequence, previous, data, lines);
            else throw new ShouldAssertException($"Unknown ledger event type {type}.");
        }
    }

    private static AccountState ApplyPosting(Guid accountId, int version, long sequence, AccountState previous, JsonElement data, List<LineState> lines)
    {
        var direction = data.GetProperty("direction").GetString()!;
        direction.ShouldBeOneOf("Debit", "Credit");
        var amount = data.GetProperty("amountMinor").GetInt64();
        amount.ShouldBeGreaterThan(0);
        data.GetProperty("currency").GetString().ShouldBe(previous.Currency);
        var balance = checked(previous.Balance + (direction == "Credit" ? amount : -amount));
        data.GetProperty("balanceAfterMinor").GetInt64().ShouldBe(balance);
        var entry = data.GetProperty("journalEntryId").GetGuid();
        lines.Add(new LineState(entry, lines.Count(l => l.Entry == entry) + 1, accountId, direction, amount, balance, sequence));
        return previous with { Balance = balance, Version = version };
    }

    private static async Task AssertAccountsAsync(NpgsqlConnection connection, Dictionary<Guid, AccountState> expected)
    {
        await using var command = new NpgsqlCommand("SELECT id,owner_id,type,system_code,currency,status,balance_minor,version,contact_email,opened_at FROM accounts", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            var id = reader.GetGuid(0);
            expected.ContainsKey(id).ShouldBeTrue("Every account must have an event stream.");
            var actual = new AccountState(reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetFieldValue<DateTimeOffset>(9));
            actual.ShouldBe(expected[id], $"Projection differs from replay for account {id}.");
            count++;
        }
        count.ShouldBe(expected.Count);
    }

    private static async Task AssertLinesAsync(NpgsqlConnection connection, List<LineState> expected)
    {
        await using var command = new NpgsqlCommand("SELECT journal_entry_id,line_no,account_id,direction,amount_minor,balance_after_minor,event_global_seq FROM journal_lines ORDER BY event_global_seq", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var actual = new List<LineState>();
        while (await reader.ReadAsync()) actual.Add(new LineState(reader.GetGuid(0), reader.GetInt32(1), reader.GetGuid(2),
            reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
        actual.ShouldBe(expected, "Journal lines must equal event replay.");
    }

    private static string? NullableString(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static Guid? NullableGuid(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetGuid() : null;
    private sealed record AccountState(Guid? Owner, string Type, string? Code, string Currency, string Status, long Balance, int Version, string? Email, DateTimeOffset OpenedAt);
    private sealed record LineState(Guid Entry, int Number, Guid Account, string Direction, long Amount, long BalanceAfter, long Sequence);
}
