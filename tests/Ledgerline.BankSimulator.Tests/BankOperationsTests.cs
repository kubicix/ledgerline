using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ledgerline.BankSimulator.Features;
using Ledgerline.BankSimulator.Infrastructure;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[assembly: AssemblyFixture(typeof(InfrastructureFixture))]

namespace Ledgerline.BankSimulator.Tests;

public sealed class BankOperationsTests(InfrastructureFixture infrastructure)
{
    private static readonly string ApiKey = Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Authorize_with_tok_ok_returns_201()
    {
        await using var host = await StartAsync();
        using var client = Client(host);
        using var response = await AuthorizeAsync(client);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await BodyAsync(response);
        body.GetProperty("status").GetString().ShouldBe("Authorized");
        body.GetProperty("authorizationId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("tok_declined", "do_not_honor")]
    [InlineData("tok_insufficient", "insufficient_funds")]
    public async Task Authorize_with_tok_declined_returns_402_with_code(string token, string code)
    {
        await using var host = await StartAsync();
        using var client = Client(host);
        using var response = await AuthorizeAsync(client, token);
        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        (await BodyAsync(response)).GetProperty("declineCode").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task Unknown_token_returns_400()
    {
        await using var host = await StartAsync();
        using var client = Client(host);
        using var response = await AuthorizeAsync(client, "unknown");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Capture_requires_authorized_state()
    {
        await using var host = await StartAsync();
        using var client = Client(host);
        var authorization = await AuthorizationIdAsync(client);
        using var voided = await PostAsync(client, $"/authorizations/{authorization}/void");
        voided.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var capture = await PostAsync(client, $"/authorizations/{authorization}/capture");
        capture.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Capture_of_tok_capture_fails_returns_402()
    {
        await using var host = await StartAsync();
        using var client = Client(host);
        var authorization = await AuthorizationIdAsync(client, "tok_capture_fails");
        using var response = await PostAsync(client, $"/authorizations/{authorization}/capture");
        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        (await BodyAsync(response)).GetProperty("declineCode").GetString().ShouldBe("capture_declined");
    }

    [Fact]
    public async Task Refund_requires_captured_state()
    {
        await using var host = await StartAsync();
        using var client = Client(host);
        var authorization = await AuthorizationIdAsync(client);
        using var missing = await PostAsync(client, "/captures/missing/refund");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var captured = await PostAsync(client, $"/authorizations/{authorization}/capture");
        var capture = (await BodyAsync(captured)).GetProperty("captureId").GetString();
        using var refunded = await PostAsync(client, $"/captures/{capture}/refund");
        refunded.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var repeated = await PostAsync(client, $"/captures/{capture}/refund");
        repeated.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Same_idempotency_key_returns_same_response_and_performs_once()
    {
        await using var host = await StartAsync();
        await using var replica = await ServiceHost<Program>.StartOnDatabaseAsync(infrastructure, host.ConnectionString,
            services => services.Configure<BankOptions>(options => options.ApiKey = ApiKey));
        using var client = Client(host);
        using var other = Client(replica);
        var authorization = await AuthorizationIdAsync(client);
        var path = $"/authorizations/{authorization}/capture";
        var responses = await Task.WhenAll(PostAsync(client, path, "capture-once"), PostAsync(other, path, "capture-once"));
        try
        {
            responses.ShouldAllBe(x => x.StatusCode == HttpStatusCode.OK);
            var bodies = await Task.WhenAll(responses.Select(x => x.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
            JsonNode.DeepEquals(JsonNode.Parse(bodies[0]), JsonNode.Parse(bodies[1])).ShouldBeTrue();
            await using var scope = host.Services.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<BankDbContext>().Operations.CountAsync(x => x.Kind == "Capture", TestContext.Current.CancellationToken)).ShouldBe(1);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Lookup_returns_recorded_operation_or_404()
    {
        await using var host = await StartAsync();
        using var client = Client(host);
        using var authorized = await AuthorizeAsync(client, key: "lookup");
        using var found = await client.GetAsync("/operations/lookup", TestContext.Current.CancellationToken);
        found.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyAsync(found);
        body.GetProperty("kind").GetString().ShouldBe("Authorize");
        body.GetProperty("httpStatus").GetInt32().ShouldBe(201);
        body.GetProperty("body").GetProperty("authorizationId").GetString().ShouldBe((await BodyAsync(authorized)).GetProperty("authorizationId").GetString());
        using var missing = await client.GetAsync("/operations/missing", TestContext.Current.CancellationToken);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Missing_api_key_returns_401()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();
        using var response = await AuthorizeAsync(client);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private Task<ServiceHost<Program>> StartAsync() => ServiceHost<Program>.StartAsync(infrastructure, "bank_ops",
        services => services.Configure<BankOptions>(options => options.ApiKey = ApiKey));
    private static HttpClient Client(ServiceHost<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }
    private static Task<HttpResponseMessage> AuthorizeAsync(HttpClient client, string token = "tok_ok", string? key = null) =>
        PostAsync(client, "/authorizations", key, new { amountMinor = 150_000, currency = "TRY", cardToken = token, reference = Guid.NewGuid().ToString() });
    private static async Task<string> AuthorizationIdAsync(HttpClient client, string token = "tok_ok")
    {
        using var response = await AuthorizeAsync(client, token);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await BodyAsync(response)).GetProperty("authorizationId").GetString()!;
    }
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? key = null, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
    private static Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
