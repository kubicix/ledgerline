using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.BuildingBlocks.Web;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.Testing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledgerline.BuildingBlocks.Tests;

public sealed class WebDefaultsTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("{\"accountId\":\"not-a-guid\",\"amountMinor\":1}")]
    [InlineData("{\"accountId\":\"00000000-0000-0000-0000-000000000001\",\"amountMinor\":9223372036854775808}")]
    public async Task Development_binding_errors_return_validation_problem(string json)
    {
        await using var app = await CreateAppAsync(environment: "Development");
        using var client = app.GetTestClient();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/binding", content, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("type").GetString().ShouldBe("urn:ledgerline:problem:validation-failed");
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Unhandled_exception_becomes_problem_details_with_trace_id()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/boom", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        problem.GetProperty("type").GetString()!.ShouldStartWith("urn:ledgerline:problem:");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("private failure");
    }

    [Fact]
    public async Task Request_without_token_gives_401()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/customer", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Role_policy_denies_with_403()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(Guid.NewGuid(), "merchant", null));
        using var response = await client.GetAsync("/customer", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void TestKey_mode_throws_at_startup_outside_Testing_environment(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Mode"] = "TestKey" });
        Should.Throw<InvalidOperationException>(() => builder.AddLedgerlineWebDefaults("test"));
    }

    [Fact]
    public async Task Token_with_wrong_audience_gives_401()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(Guid.NewGuid(), "customer", null,
            "another-api", DateTimeOffset.UtcNow.AddMinutes(5)));
        using var response = await client.GetAsync("/customer", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Expired_token_gives_401()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(Guid.NewGuid(), "customer", null,
            "ledgerline-api", DateTimeOffset.UtcNow.AddMinutes(-1)));
        using var response = await client.GetAsync("/customer", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("merchant")]
    [InlineData("admin")]
    public async Task Roles_claim_maps_to_policies(string role)
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(Guid.NewGuid(), role, null));
        using var response = await client.GetAsync("/" + role, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task No_dev_header_scheme_is_registered()
    {
        await using var app = await CreateAppAsync();
        var schemes = await app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        schemes.ShouldNotContain(scheme => scheme.Name == "DevHeaders");
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Dev-User", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "customer");
        using var response = await client.GetAsync("/customer", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_live_returns_200_and_ready_reflects_checks()
    {
        var check = new MutableCheck();
        await using var app = await CreateAppAsync(check);
        using var client = app.GetTestClient();
        using var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        check.Fail();
        using var unhealthy = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        unhealthy.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        using var stillLive = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        stillLive.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Current_user_reads_id_email_and_roles_from_JWT()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        var id = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(id, "merchant, customer", "user@example.test"));
        var user = await client.GetFromJsonAsync<JsonElement>("/me", TestContext.Current.CancellationToken);
        user.GetProperty("id").GetGuid().ShouldBe(id);
        user.GetProperty("email").GetString().ShouldBe("user@example.test");
        user.GetProperty("customer").GetBoolean().ShouldBeTrue();
    }

    private static async Task<WebApplication> CreateAppAsync(MutableCheck? check = null, string environment = "Testing")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Mode"] = environment == "Testing" ? "TestKey" : "Oidc",
            ["Auth:Authority"] = TestTokens.Issuer,
            ["Auth:Audience"] = "ledgerline-api",
            ["Auth:TestSigningKey"] = TestTokens.SigningKey
        });
        builder.AddLedgerlineWebDefaults("test");
        if (check is not null) builder.Services.AddHealthChecks().AddCheck("dependency", check);
        var app = builder.Build();
        app.UseLedgerlineWebDefaults();
        app.MapGet("/boom", (Func<IResult>)(() => throw new InvalidOperationException("private failure")));
        app.MapPost("/binding", (BindingRequest request) => Results.Ok(request));
        app.MapGet("/customer", () => Results.Ok()).RequireAuthorization(Policies.Customer);
        app.MapGet("/merchant", () => Results.Ok()).RequireAuthorization(Policies.Merchant);
        app.MapGet("/admin", () => Results.Ok()).RequireAuthorization(Policies.Admin);
        app.MapGet("/me", (ICurrentUser user) => Results.Ok(new { user.Id, user.Email, Customer = user.IsInRole("customer") })).RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    public sealed record BindingRequest(Guid AccountId, long AmountMinor);

    private sealed class MutableCheck : IHealthCheck
    {
        private int failed;
        public void Fail() => Interlocked.Exchange(ref failed, 1);
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(Volatile.Read(ref failed) == 0 ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy());
    }
}
