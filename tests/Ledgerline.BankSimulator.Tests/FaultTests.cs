using System.Net;
using System.Net.Http.Json;
using Ledgerline.BankSimulator.Infrastructure;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.BankSimulator.Tests;

public sealed class FaultTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Error500_fault_fails_next_n_requests_without_performing()
    {
        await using var host = await BankTestSetup.StartAsync(infrastructure);
        using var client = BankTestSetup.Client(host);
        await SetFaultAsync(client, "Error500", 2);
        for (var i = 0; i < 2; i++)
        {
            using var response = await BankTestSetup.AuthorizeAsync(client);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<BankDbContext>().Operations.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task SuccessThenTimeout_performs_operation_and_lookup_finds_it()
    {
        await using var host = await BankTestSetup.StartAsync(infrastructure);
        using var client = BankTestSetup.Client(host);
        await SetFaultAsync(client, "SuccessThenTimeout", 1);
        using var impatient = BankTestSetup.Client(host);
        impatient.Timeout = TimeSpan.FromMilliseconds(500);
        await Should.ThrowAsync<TaskCanceledException>(() => BankTestSetup.AuthorizeAsync(impatient, "performed"));
        using var lookup = await client.GetAsync("/operations/performed", TestContext.Current.CancellationToken);
        lookup.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BankTestSetup.BodyAsync(lookup)).GetProperty("httpStatus").GetInt32().ShouldBe(201);
    }

    [Fact]
    public async Task Timeout_fault_does_not_perform_operation()
    {
        await using var host = await BankTestSetup.StartAsync(infrastructure);
        using var client = BankTestSetup.Client(host);
        await SetFaultAsync(client, "Timeout", 1);
        using var impatient = BankTestSetup.Client(host);
        impatient.Timeout = TimeSpan.FromMilliseconds(500);
        await Should.ThrowAsync<TaskCanceledException>(() => BankTestSetup.AuthorizeAsync(impatient, "not-performed"));
        using var lookup = await client.GetAsync("/operations/not-performed", TestContext.Current.CancellationToken);
        lookup.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Fault_expires_after_count()
    {
        await using var host = await BankTestSetup.StartAsync(infrastructure);
        using var client = BankTestSetup.Client(host);
        await SetFaultAsync(client, "Error500", 1);
        using var failed = await BankTestSetup.AuthorizeAsync(client);
        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        using var successful = await BankTestSetup.AuthorizeAsync(client);
        successful.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private static async Task SetFaultAsync(HttpClient client, string mode, int count)
    {
        using var response = await client.PutAsJsonAsync("/faults", new { operation = "Authorize", mode, count, delayMs = 2000 }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
