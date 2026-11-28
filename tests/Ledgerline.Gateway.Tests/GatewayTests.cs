using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledgerline.Gateway.Tests;

public sealed class GatewayTests
{
    [Fact]
    public async Task Stalled_upstream_returns_complete_504_problem()
    {
        await using var scenario = await Scenario.StartAsync(new() { ["ReverseProxy:Routes:accounts:Timeout"] = "00:00:00.050" });
        using var response = await scenario.Client.GetAsync("/api/accounts/slow", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("status").GetInt32().ShouldBe(504);
        problem.GetProperty("type").GetString()!.ShouldStartWith("urn:ledgerline:problem:");
        problem.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("accounts/123", "accounts/123")]
    [InlineData("ledger/trial-balance", "ledger/trial-balance")]
    [InlineData("topups", "topups")]
    [InlineData("transfers", "transfers")]
    [InlineData("payments/123/capture", "payments/123/capture")]
    [InlineData("risk/rules", "risk/rules")]
    [InlineData("notifications", "notifications")]
    [InlineData("reporting/accounts/123/statement", "accounts/123/statement")]
    [InlineData("reconciliation/runs/2026-11-13/items", "runs/2026-11-13/items")]
    public async Task Routes_map_public_paths_to_upstream_paths(string path, string expected)
    {
        await using var scenario = await Scenario.StartAsync();
        using var response = await scenario.Client.GetAsync("/api/" + path, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("path").GetString().ShouldBe("/" + expected);
    }

    [Fact]
    public async Task Unauthenticated_request_is_rejected_with_401_before_reaching_upstream()
    {
        await using var scenario = await Scenario.StartAsync();
        scenario.Client.DefaultRequestHeaders.Authorization = null;
        using var response = await scenario.Client.GetAsync("/api/accounts", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        scenario.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Admin_route_denies_customer_with_403()
    {
        await using var scenario = await Scenario.StartAsync();
        scenario.Client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(Guid.NewGuid(), "customer", null));
        using var response = await scenario.Client.GetAsync("/api/risk/rules", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        scenario.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Reconciliation_route_denies_customer_with_403()
    {
        await using var scenario = await Scenario.StartAsync();
        scenario.Client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(Guid.NewGuid(), "customer", null));
        using var response = await scenario.Client.GetAsync("/api/reconciliation/runs", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        scenario.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Phase2_routes_without_their_services_return_503_and_leave_the_rest_working()
    {
        // As in the default deployment: no reporting or reconciliation host exists.
        await using var scenario = await Scenario.StartAsync(new()
        {
            ["ReverseProxy:Clusters:reporting:Destinations:primary:Address"] = "http://127.0.0.1:1",
            ["ReverseProxy:Clusters:reconciliation:Destinations:primary:Address"] = "http://127.0.0.1:1"
        });
        foreach (var path in new[] { "/api/reporting/accounts/123/statement", "/api/reconciliation/runs" })
        {
            using var failed = await scenario.Client.GetAsync(path, TestContext.Current.CancellationToken);
            failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }
        using var healthy = await scenario.Client.GetAsync("/api/accounts", TestContext.Current.CancellationToken);
        healthy.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var ready = await scenario.Client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Correlation_id_is_generated_forwarded_and_returned()
    {
        await using var scenario = await Scenario.StartAsync();
        scenario.Client.DefaultRequestHeaders.Add("X-Correlation-Id", "invalid");
        using var response = await scenario.Client.GetAsync("/api/accounts", TestContext.Current.CancellationToken);
        var id = response.Headers.GetValues("X-Correlation-Id").Single();
        Guid.TryParse(id, out var parsed).ShouldBeTrue();
        parsed.Version.ShouldBe(7);
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("correlation").GetString().ShouldBe(id);
    }

    [Fact]
    public async Task Valid_incoming_correlation_id_is_preserved()
    {
        await using var scenario = await Scenario.StartAsync();
        var id = Guid.NewGuid().ToString();
        scenario.Client.DefaultRequestHeaders.Add("X-Correlation-Id", id);
        using var response = await scenario.Client.GetAsync("/api/accounts", TestContext.Current.CancellationToken);
        response.Headers.GetValues("X-Correlation-Id").Single().ShouldBe(id);
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("correlation").GetString().ShouldBe(id);
    }

    [Fact]
    public async Task Authorization_and_idempotency_headers_are_forwarded()
    {
        await using var scenario = await Scenario.StartAsync();
        scenario.Client.DefaultRequestHeaders.Add("Idempotency-Key", "a-key");
        using var response = await scenario.Client.PostAsync("/api/topups", JsonContent.Create(new { amountMinor = 100 }), TestContext.Current.CancellationToken);
        var echo = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        echo.GetProperty("method").GetString().ShouldBe("POST");
        echo.GetProperty("authorization").GetString().ShouldBe(scenario.Client.DefaultRequestHeaders.Authorization!.ToString());
        echo.GetProperty("idempotency").GetString().ShouldBe("a-key");
    }

    [Fact]
    public async Task Rate_limit_returns_429_with_retry_after()
    {
        await using var scenario = await Scenario.StartAsync(new() { ["RateLimiting:TokenLimit"] = "1", ["RateLimiting:TokensPerPeriod"] = "1", ["RateLimiting:PeriodSeconds"] = "60" });
        using var first = await scenario.Client.GetAsync("/api/accounts", TestContext.Current.CancellationToken);
        using var second = await scenario.Client.GetAsync("/api/accounts", TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        second.Headers.RetryAfter.ShouldNotBeNull();
        (await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("type").GetString().ShouldBe("urn:ledgerline:problem:rate-limited");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Body_larger_than_64kb_returns_413(bool unknownLength)
    {
        await using var scenario = await Scenario.StartAsync();
        using HttpContent content = unknownLength ? new UnknownLengthContent() : new StringContent(new string('x', 65537));
        using var response = await scenario.Client.PostAsync("/api/topups", content, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        scenario.Calls.ShouldBe(0);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[65537]).AsTask();
    }

    [Fact]
    public async Task Upstream_down_returns_503_for_that_route_only()
    {
        await using var scenario = await Scenario.StartAsync(new() { ["ReverseProxy:Clusters:payments:Destinations:primary:Address"] = "http://127.0.0.1:1" });
        using var failed = await scenario.Client.GetAsync("/api/payments/123", TestContext.Current.CancellationToken);
        failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        using var healthy = await scenario.Client.GetAsync("/api/accounts", TestContext.Current.CancellationToken);
        healthy.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var ready = await scenario.Client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_endpoints_are_not_proxied()
    {
        await using var scenario = await Scenario.StartAsync();
        scenario.Client.DefaultRequestHeaders.Authorization = null;
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var response = await scenario.Client.GetAsync(path, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        scenario.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Post_is_not_retried_on_upstream_failure()
    {
        await using var scenario = await Scenario.StartAsync();
        using var response = await scenario.Client.PostAsync("/api/topups/fail", JsonContent.Create(new { }), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        scenario.Calls.ShouldBe(1);
    }

    private sealed class Scenario(WebApplication upstream, WebApplicationFactory<Program> factory) : IAsyncDisposable
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal HttpClient Client { get; } = factory.CreateClient();

        internal static async Task<Scenario> StartAsync(Dictionary<string, string?>? overrides = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var upstream = builder.Build();
            Scenario? scenario = null;
            upstream.MapGet("/health/ready", () => Results.Ok());
            upstream.Map("/{**path}", async (HttpContext context) =>
            {
                Interlocked.Increment(ref scenario!.calls);
                if (context.Request.Path.Value!.EndsWith("/fail", StringComparison.Ordinal)) return (IResult)Results.StatusCode(500);
                if (context.Request.Path.Value.EndsWith("/slow", StringComparison.Ordinal))
                    await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted);
                return Results.Json(new { method = context.Request.Method, path = context.Request.Path.Value,
                    correlation = context.Request.Headers["X-Correlation-Id"].ToString(), authorization = context.Request.Headers.Authorization.ToString(),
                    idempotency = context.Request.Headers["Idempotency-Key"].ToString() });
            });
            await upstream.StartAsync(TestContext.Current.CancellationToken);
            var address = upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var config = new Dictionary<string, string?> { ["Auth:Mode"] = "TestKey", ["Auth:Authority"] = TestTokens.Issuer,
                ["Auth:Audience"] = "ledgerline-api", ["Auth:TestSigningKey"] = TestTokens.SigningKey };
            foreach (var cluster in new[] { "accounts", "payments", "risk", "notifications", "reporting", "reconciliation" })
                config[$"ReverseProxy:Clusters:{cluster}:Destinations:primary:Address"] = address;
            foreach (var pair in overrides ?? []) config[pair.Key] = pair.Value;
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web.UseEnvironment("Testing")
                .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(config)));
            scenario = new(upstream, factory);
            scenario.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(Guid.NewGuid(), "customer,merchant,admin", null));
            return scenario;
        }

        public async ValueTask DisposeAsync() { Client.Dispose(); await factory.DisposeAsync(); await upstream.DisposeAsync(); }
    }
}
