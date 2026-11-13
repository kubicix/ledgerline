using System.Net;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Payments.IntegrationTests;

public sealed class PaymentsApiTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Post_transfer_returns_202_with_location_and_processing_status()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        using var client = host.CreateClient(owner);
        using var response = await PaymentTestSetup.TransferAsync(client, from, to);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await PaymentTestSetup.BodyAsync(response);
        response.Headers.Location!.OriginalString.ShouldBe("/payments/" + body.GetProperty("id").GetGuid());
        body.GetProperty("status").GetString().ShouldBe("Processing");
        body.GetProperty("amountMinor").GetInt64().ShouldBe(2500);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_000_000_01)]
    public async Task Post_with_invalid_amount_returns_400(long amount)
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        using var client = host.CreateClient(Guid.NewGuid());
        using var response = await PaymentTestSetup.TransferAsync(client, Guid.NewGuid(), Guid.NewGuid(), amount);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Transfer_from_unknown_account_returns_404()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        using var client = host.CreateClient(Guid.NewGuid());
        using var response = await PaymentTestSetup.TransferAsync(client, Guid.NewGuid(), Guid.NewGuid());
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Transfer_from_foreign_account_returns_404()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        using var client = host.CreateClient(Guid.NewGuid());
        using var response = await PaymentTestSetup.TransferAsync(client, from, to);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Transfer_to_same_account_returns_400()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var account = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        using var client = host.CreateClient(owner);
        using var response = await PaymentTestSetup.TransferAsync(client, account, account);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Transfer_with_currency_mismatch_returns_400()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid(), "EUR");
        using var client = host.CreateClient(owner);
        using var response = await PaymentTestSetup.TransferAsync(client, from, to);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Same_idempotency_key_returns_same_payment()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        using var client = host.CreateClient(owner);
        using var first = await PaymentTestSetup.TransferAsync(client, from, to, key: "same");
        using var second = await PaymentTestSetup.TransferAsync(client, from, to, key: "same");
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await PaymentTestSetup.BodyAsync(second)).GetProperty("id").GetGuid().ShouldBe((await PaymentTestSetup.BodyAsync(first)).GetProperty("id").GetGuid());
        using var conflict = await PaymentTestSetup.TransferAsync(client, from, to, 5000, "same");
        conflict.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Same_idempotency_key_sent_concurrently_creates_one_payment()
    {
        await using var host = await PaymentTestSetup.StartAsync(infrastructure);
        await using var probe = await PaymentTestSetup.ProbeAsync(host);
        var owner = Guid.NewGuid();
        var from = await PaymentTestSetup.AddAccountAsync(host, probe, owner);
        var to = await PaymentTestSetup.AddAccountAsync(host, probe, Guid.NewGuid());
        using var client = host.CreateClient(owner);
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PaymentTestSetup.TransferAsync(client, from, to, key: "concurrent")));
        try
        {
            responses.ShouldAllBe(x => x.StatusCode == HttpStatusCode.Accepted || x.StatusCode == HttpStatusCode.Conflict);
            foreach (var pending in responses.Where(x => x.StatusCode == HttpStatusCode.Conflict))
            {
                pending.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
                (await PaymentTestSetup.BodyAsync(pending)).GetProperty("type").GetString().ShouldBe("urn:ledgerline:problem:request-in-progress");
            }
            using var replay = await PaymentTestSetup.TransferAsync(client, from, to, key: "concurrent");
            replay.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            var bodies = await Task.WhenAll(responses.Where(x => x.StatusCode == HttpStatusCode.Accepted).Append(replay).Select(PaymentTestSetup.BodyAsync));
            bodies.Select(x => x.GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(1);
            await using var scope = host.Services.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<Ledgerline.Payments.Infrastructure.PaymentsDbContext>().Payments.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }
}
