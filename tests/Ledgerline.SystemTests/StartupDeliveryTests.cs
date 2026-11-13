using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using AccountsProgram = Ledgerline.Accounts.Program;
using PaymentsProgram = Ledgerline.Payments.Program;

[assembly: AssemblyFixture(typeof(InfrastructureFixture))]

namespace Ledgerline.SystemTests;

public sealed class StartupDeliveryTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Wallet_opened_before_first_payments_start_is_eventually_replicated()
    {
        await DeclareComposeArchiveAsync();
        await using var accounts = await ServiceHost<AccountsProgram>.StartAsync(infrastructure, "early_accounts",
            services => services.AddSingleton(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri }));
        using var client = accounts.CreateClient(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/accounts")
        {
            Content = JsonContent.Create(new { type = "CustomerWallet", currency = "TRY" })
        };
        request.Headers.Add("Idempotency-Key", "early-wallet");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
        await Eventually.ShouldAsync(async () =>
        {
            await using var scope = accounts.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AccountsDbContext>().Set<OutboxMessage>()
                .AnyAsync(x => x.Type == "accounts.event.account-opened.v1" && (x.ProcessedAt != null || x.Attempts > 0), TestContext.Current.CancellationToken);
        });
        await using var payments = await ServiceHost<PaymentsProgram>.StartAsync(infrastructure, "late_payments",
            services => services.AddSingleton(new RabbitMqOptions { Uri = infrastructure.RabbitMqUri }));
        await Eventually.ShouldAsync(async () =>
        {
            await using var scope = payments.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().AccountRefs.AnyAsync(x => x.AccountId == id, TestContext.Current.CancellationToken);
        });
    }

    private async Task DeclareComposeArchiveAsync()
    {
        using var definitions = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "local-events.json")));
        await using var connection = await new ConnectionFactory { Uri = new Uri(infrastructure.RabbitMqUri) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        foreach (var exchange in definitions.RootElement.GetProperty("exchanges").EnumerateArray())
            await channel.ExchangeDeclareAsync(exchange.GetProperty("name").GetString()!, exchange.GetProperty("type").GetString()!, durable: true);
        foreach (var queue in definitions.RootElement.GetProperty("queues").EnumerateArray())
            await channel.QueueDeclareAsync(queue.GetProperty("name").GetString()!, true, false, false,
                queue.GetProperty("arguments").EnumerateObject().ToDictionary(x => x.Name, x => (object?)x.Value.GetString()));
        foreach (var binding in definitions.RootElement.GetProperty("bindings").EnumerateArray())
            await channel.QueueBindAsync(binding.GetProperty("destination").GetString()!, binding.GetProperty("source").GetString()!, binding.GetProperty("routing_key").GetString()!);
    }
}
