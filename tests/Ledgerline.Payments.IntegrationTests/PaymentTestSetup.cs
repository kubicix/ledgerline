using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[assembly: AssemblyFixture(typeof(InfrastructureFixture))]

namespace Ledgerline.Payments.IntegrationTests;

internal static class PaymentTestSetup
{
    internal static Task<ServiceHost<Program>> StartAsync(InfrastructureFixture infrastructure) => ServiceHost<Program>.StartAsync(infrastructure, "payments");
    internal static Task<BrokerProbe> ProbeAsync(ServiceHost<Program> host) => BrokerProbe.CreateAsync(
        host.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.command.#", "payments.event.#");

    internal static async Task<Guid> AddAccountAsync(ServiceHost<Program> host, BrokerProbe probe, Guid owner, string currency = "TRY")
    {
        var id = Guid.NewGuid();
        await probe.PublishAsync(new AccountOpened(id, owner, "CustomerWallet", currency, "synthetic@example.test", DateTimeOffset.UtcNow), id);
        await Eventually.ShouldAsync(async () =>
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().AccountRefs.AnyAsync(x => x.AccountId == id);
        });
        return id;
    }

    internal static async Task<HttpResponseMessage> TransferAsync(HttpClient client, Guid from, Guid to, long amount = 2500, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/transfers")
        {
            Content = JsonContent.Create(new { fromAccountId = from, toAccountId = to, amountMinor = amount, currency = "TRY" })
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    internal static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
