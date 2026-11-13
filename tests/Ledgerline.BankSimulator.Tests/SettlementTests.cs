using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Ledgerline.BankSimulator.Infrastructure;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.BankSimulator.Tests;

public sealed class SettlementTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Settlement_lists_captures_and_refunds_of_the_day_only()
    {
        await using var host = await BankTestSetup.StartAsync(infrastructure);
        using var client = BankTestSetup.Client(host);
        var today = await BankTestSetup.CaptureAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/captures/{today}/refund");
        request.Headers.Add("Idempotency-Key", "refund");
        using var refund = await client.SendAsync(request, TestContext.Current.CancellationToken);
        refund.StatusCode.ShouldBe(HttpStatusCode.OK);
        var refundId = (await BankTestSetup.BodyAsync(refund)).GetProperty("refundId").GetString()!;
        var yesterday = await BankTestSetup.CaptureAsync(client);
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BankDbContext>().Operations.Where(x => x.Kind == "Capture" && x.CaptureId == yesterday)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.OccurredAt, DateTimeOffset.UtcNow.AddDays(-1)), TestContext.Current.CancellationToken);
        var csv = await CsvAsync(client);
        csv.ShouldContain(today);
        csv.ShouldContain(refundId);
        csv.ShouldNotContain(yesterday);
        Lines(csv).Length.ShouldBe(3);
    }

    [Fact]
    public async Task Settlement_csv_has_header_and_spec_columns()
    {
        await using var host = await BankTestSetup.StartAsync(infrastructure);
        using var client = BankTestSetup.Client(host);
        var capture = await BankTestSetup.CaptureAsync(client);
        var lines = Lines(await CsvAsync(client));
        lines[0].ShouldBe("record_type,bank_id,reference,amount_minor,currency,occurred_at");
        var columns = lines[1].Split(',');
        columns.Length.ShouldBe(6);
        columns[0].ShouldBe("CAPTURE");
        columns[1].ShouldBe(capture);
        Guid.TryParse(columns[2], out _).ShouldBeTrue();
        columns[3].ShouldBe("150000");
        columns[4].ShouldBe("TRY");
        DateTimeOffset.TryParse(columns[5], CultureInfo.InvariantCulture, DateTimeStyles.None, out _).ShouldBeTrue();
        await using var scope = host.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<BankDbContext>().Operations.SingleAsync(x => x.Kind == "Capture", TestContext.Current.CancellationToken);
        DateTimeOffset.Parse(columns[5], CultureInfo.InvariantCulture).ShouldBe(stored.ResponseBody.GetProperty("capturedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Tampering_drops_or_alters_rows_in_file_only()
    {
        await using var host = await BankTestSetup.StartAsync(infrastructure);
        using var client = BankTestSetup.Client(host);
        var drop = await BankTestSetup.CaptureAsync(client);
        var alter = await BankTestSetup.CaptureAsync(client);
        using var tampered = await client.PutAsJsonAsync("/settlement-tampering", new
        {
            date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            dropCaptureId = drop, alterCaptureId = alter, alteredAmountMinor = 123
        }, TestContext.Current.CancellationToken);
        tampered.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var csv = await CsvAsync(client);
        csv.ShouldNotContain(drop);
        csv.ShouldContain(alter);
        csv.ShouldContain(",123,TRY,");
        await using var scope = host.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<BankDbContext>().Operations.Where(x => x.Kind == "Capture").ToListAsync(TestContext.Current.CancellationToken);
        stored.Count.ShouldBe(2);
        stored.ShouldAllBe(x => x.AmountMinor == 150_000);
    }

    private static string[] Lines(string csv) => csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static Task<string> CsvAsync(HttpClient client) => client.GetStringAsync(
        "/settlements/" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);
}
