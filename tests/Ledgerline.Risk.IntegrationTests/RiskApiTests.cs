using System.Net;
using System.Net.Http.Json;
using Ledgerline.BuildingBlocks;
using Ledgerline.Risk.Domain;
using Ledgerline.Risk.Infrastructure;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Ledgerline.Risk.IntegrationTests;

public sealed class RiskApiTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Update_rule_requires_admin()
    {
        await using var host = await StartAsync();
        var id = await RuleIdAsync(host, RuleCode.MaxSingleAmount);
        using var customer = host.CreateClient(Guid.NewGuid());
        using var denied = await customer.PutAsJsonAsync($"/risk/rules/{id}", new { thresholdMinor = 100, enabled = true }, TestContext.Current.CancellationToken);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");
        using var allowed = await admin.PutAsJsonAsync($"/risk/rules/{id}", new { thresholdMinor = 100, enabled = true }, TestContext.Current.CancellationToken);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(RuleCode.MaxSingleAmount, 100L, 10, 60)]
    [InlineData(RuleCode.VelocityLimit, 100L, 10, 60)]
    [InlineData(RuleCode.MaxSingleAmount, null, null, null)]
    [InlineData(RuleCode.VelocityLimit, null, 0, 60)]
    public async Task Update_rule_rejects_fields_that_do_not_apply(RuleCode code, long? threshold, int? maxCount, int? window)
    {
        await using var host = await StartAsync();
        var id = await RuleIdAsync(host, code);
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");
        using var response = await admin.PutAsJsonAsync($"/risk/rules/{id}", new { thresholdMinor = threshold, maxCount, windowSeconds = window, enabled = true }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Updated_rule_takes_effect_after_cache_expiry()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "risk_cache", services =>
        {
            services.RemoveAll<RuleCache>();
            services.AddSingleton(provider => new RuleCache(provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<ILogger<RuleCache>>(), TimeSpan.FromMilliseconds(100)));
        });
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<RuleCache>();
        var id = await RuleIdAsync(host, RuleCode.MaxSingleAmount);
        (await cache.GetAsync(db, TestContext.Current.CancellationToken)).Single(x => x.Id == id).ThresholdMinor.ShouldBe(5_000_000);
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");
        using var updated = await admin.PutAsJsonAsync($"/risk/rules/{id}", new { thresholdMinor = 100, enabled = true }, TestContext.Current.CancellationToken);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Eventually.ShouldAsync(async () => (await cache.GetAsync(db, TestContext.Current.CancellationToken)).Single(x => x.Id == id).ThresholdMinor == 100);
    }

    [Fact]
    public async Task Get_decision_returns_404_for_unknown_payment()
    {
        await using var host = await StartAsync();
        using var admin = host.CreateClient(Guid.NewGuid(), "admin");
        using var response = await admin.GetAsync($"/risk/decisions/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private Task<ServiceHost<Program>> StartAsync() => ServiceHost<Program>.StartAsync(infrastructure, "risk_api");
    private static async Task<Guid> RuleIdAsync(ServiceHost<Program> host, RuleCode code)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RiskDbContext>().Rules
            .Where(x => x.Code == code && (x.Currency == "TRY" || x.Currency == null)).Select(x => x.Id).SingleAsync(TestContext.Current.CancellationToken);
    }
}
