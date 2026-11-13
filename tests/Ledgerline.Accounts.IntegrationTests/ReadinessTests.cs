using System.Net;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Accounts.IntegrationTests;

[CollectionDefinition("Dependency lifecycle", DisableParallelization = true)]
public sealed class DependencyLifecycle;

[Collection("Dependency lifecycle")]
public sealed class ReadinessTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Unavailable_database_fails_readiness_but_liveness_stays_healthy()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "db_health", services =>
            services.AddDbContext<AccountsDbContext>(options => options.UseNpgsql("Host=127.0.0.1;Port=1;Database=unavailable;Username=synthetic;Timeout=1")));
        using var client = host.CreateClient();
        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        using var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Broker_outage_fails_readiness_and_recovers()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "broker_health");
        using var client = host.CreateClient();
        await infrastructure.StopRabbitMqAsync();
        try
        {
            await Eventually.ShouldAsync(async () =>
            {
                using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
                return response.StatusCode == HttpStatusCode.ServiceUnavailable;
            });
            using var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
            live.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally { await infrastructure.StartRabbitMqAsync(); }
        await Eventually.ShouldAsync(async () =>
        {
            using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            return response.StatusCode == HttpStatusCode.OK;
        }, TimeSpan.FromSeconds(30));
    }
}
