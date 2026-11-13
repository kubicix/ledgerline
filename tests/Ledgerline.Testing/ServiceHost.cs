using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Reflection;

namespace Ledgerline.Testing;

public sealed class ServiceHost<TProgram> : IAsyncDisposable where TProgram : class
{
    private static readonly Type ContextType = typeof(TProgram).Assembly.GetTypes().Single(t => !t.IsAbstract && typeof(DbContext).IsAssignableFrom(t));
    private readonly WebApplicationFactory<TProgram> factory;
    public IServiceProvider Services => factory.Services;
    public string ConnectionString { get; }

    private ServiceHost(WebApplicationFactory<TProgram> factory, string connectionString)
    {
        this.factory = factory;
        ConnectionString = connectionString;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "The plan requires ServiceHost<TProgram>.StartAsync as the shared test host API.")]
    public static async Task<ServiceHost<TProgram>> StartAsync(InfrastructureFixture infrastructure,
        string databasePrefix, Action<IServiceCollection>? overrides = null)
    {
        var assembly = typeof(TProgram).Assembly;
        var contextType = ContextType;
        var migrate = assembly.GetType(contextType.Namespace + ".MigrateCommand", throwOnError: true)!
            .GetMethod("RunAsync", [typeof(string), typeof(CancellationToken)])
            ?? throw new InvalidOperationException("The service migration entry point is missing.");
        var cs = await DatabaseFactory.CreateMigratedAsync(infrastructure, databasePrefix, contextType,
            connection => (Task)migrate.Invoke(null, [connection, CancellationToken.None])!);
        return await StartOnDatabaseAsync(infrastructure, cs, overrides);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "Shared-database replica and crash tests require the generic service host factory.")]
    public static async Task<ServiceHost<TProgram>> StartOnDatabaseAsync(InfrastructureFixture infrastructure,
        string cs, Action<IServiceCollection>? overrides = null)
    {
        var rabbitMqUri = await infrastructure.CreateRabbitMqUriAsync(new NpgsqlConnectionStringBuilder(cs).Database!);
        var factory = new ConfiguredFactory(rabbitMqUri, cs, infrastructure.RedisConnectionString, overrides);
        try
        {
            _ = factory.Services;
            return new ServiceHost<TProgram>(factory, cs);
        }
        catch
        {
            await factory.DisposeAsync();
            throw;
        }
    }

    public HttpClient CreateClient(Guid? userId = null, string roles = "customer", string? email = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (userId is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(userId.Value, roles, email));
        }
        return client;
    }

    public ValueTask DisposeAsync() => factory.DisposeAsync();

    private static void ConfigurePool<TDbContext>(IServiceCollection services, string cs) where TDbContext : DbContext
    {
        services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(cs));
        services.AddDbContext<TDbContext>((provider, options) => options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
    }

    private sealed class ConfiguredFactory(string rabbitMqUri, string cs, string redisConnectionString,
        Action<IServiceCollection>? overrides) : WebApplicationFactory<TProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(Path.GetDirectoryName(typeof(TProgram).Assembly.Location)!);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = cs,
                ["ConnectionStrings:Redis"] = redisConnectionString,
                // M1 fixtures retain their walking-skeleton flow; M2 flow tests explicitly enable the full saga.
                ["Saga:RiskEnabled"] = "false",
                ["Saga:BankEnabled"] = "false",
                ["Messaging:RabbitMq:Uri"] = rabbitMqUri,
                ["Auth:Mode"] = "TestKey",
                ["Auth:Authority"] = TestTokens.Issuer,
                ["Auth:Audience"] = "ledgerline-api",
                ["Auth:TestSigningKey"] = TestTokens.SigningKey
            }));
            builder.ConfigureTestServices(services =>
            {
                typeof(ServiceHost<TProgram>).GetMethod(nameof(ConfigurePool), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(ContextType).Invoke(null, [services, cs]);
                overrides?.Invoke(services);
            });
        }
    }
}
