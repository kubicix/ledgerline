using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.BankSimulator.Features;
using Ledgerline.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.BankSimulator.Tests;

internal static class BankTestSetup
{
    private static readonly string ApiKey = Guid.NewGuid().ToString("N");
    internal static Task<ServiceHost<Program>> StartAsync(InfrastructureFixture infrastructure) =>
        ServiceHost<Program>.StartAsync(infrastructure, "bank_faults", services => services.Configure<BankOptions>(options => options.ApiKey = ApiKey));

    internal static HttpClient Client(ServiceHost<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    internal static async Task<HttpResponseMessage> AuthorizeAsync(HttpClient client, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/authorizations")
        {
            Content = JsonContent.Create(new { amountMinor = 150_000, currency = "TRY", cardToken = "tok_ok", reference = Guid.NewGuid().ToString() })
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    internal static async Task<string> CaptureAsync(HttpClient client)
    {
        using var authorization = await AuthorizeAsync(client);
        authorization.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await BodyAsync(authorization)).GetProperty("authorizationId").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/authorizations/{id}/capture");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var captured = await client.SendAsync(request, TestContext.Current.CancellationToken);
        captured.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await BodyAsync(captured)).GetProperty("captureId").GetString()!;
    }

    internal static Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
