using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ledgerline.Accounts.IntegrationTests;

public sealed class RebuildTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Projections_rebuilt_from_events_equal_live_projections()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "rebuild");
        var first = await OpenViaApiAsync(host);
        var second = await OpenViaApiAsync(host);
        await PostAsync(host, Entries.TopUp(Ids.New(), Ids.New(), first, Money.Create(1000, "TRY")));
        await PostAsync(host, Entries.Transfer(Ids.New(), Ids.New(), first, second, Money.Create(250, "TRY")));
        await PostAsync(host, Entries.TopUp(Ids.New(), Ids.New(), second, Money.Create(50, "TRY")));
        var before = await SnapshotAsync(host.ConnectionString);
        await RebuildProjectionsCommand.RunAsync(host.ConnectionString, TestContext.Current.CancellationToken);
        (await SnapshotAsync(host.ConnectionString)).ShouldBe(before);
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    private static async Task<Guid> OpenViaApiAsync(ServiceHost<Program> host)
    {
        using var client = host.CreateClient(Ids.New());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/accounts") { Content = JsonContent.Create(new { Type = "CustomerWallet", Currency = "TRY" }) };
        request.Headers.Add("Idempotency-Key", Ids.New().ToString("N"));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
    }

    private static async Task PostAsync(ServiceHost<Program> host, PostingRequest request)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        (await scope.ServiceProvider.GetRequiredService<ILedger>().PostAsync(request, TestContext.Current.CancellationToken)).ShouldBeOfType<PostingResult.Posted>();
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> SnapshotAsync(string cs)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        foreach (var (table, order) in new[] { ("accounts", "id"), ("journal_entries", "id"), ("journal_lines", "journal_entry_id,line_no"), ("holds", "payment_id") })
        {
            rows.Add(table);
            await using var command = new NpgsqlCommand($"SELECT row_to_json(t)::text FROM (SELECT * FROM {table} ORDER BY {order}) t", connection);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken)) rows.Add(reader.GetString(0));
        }
        return string.Join('\n', rows);
    }
}
