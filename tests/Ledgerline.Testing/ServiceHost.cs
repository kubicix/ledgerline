using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ledgerline.Testing;

public sealed class ServiceHost<TProgram> : IAsyncDisposable where TProgram : class
{
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
        var contextType = assembly.GetTypes().Single(t => !t.IsAbstract && typeof(DbContext).IsAssignableFrom(t));
        var migrate = assembly.GetType(contextType.Namespace + ".MigrateCommand", throwOnError: true)!
            .GetMethod("RunAsync", [typeof(string), typeof(CancellationToken)])
            ?? throw new InvalidOperationException("The service migration entry point is missing.");
        var cs = await DatabaseFactory.CreateMigratedAsync(infrastructure, databasePrefix, contextType,
            connection => (Task)migrate.Invoke(null, [connection, CancellationToken.None])!);
        var rabbitMqUri = await infrastructure.CreateRabbitMqUriAsync(new NpgsqlConnectionStringBuilder(cs).Database!);
        var factory = new ConfiguredFactory(rabbitMqUri, cs, overrides);
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
            client.DefaultRequestHeaders.Add("X-Dev-User", userId.Value.ToString());
            client.DefaultRequestHeaders.Add("X-Dev-Roles", roles);
            if (email is not null) client.DefaultRequestHeaders.Add("X-Dev-Email", email);
        }
        return client;
    }

    public ValueTask DisposeAsync() => factory.DisposeAsync();

    private sealed class ConfiguredFactory(string rabbitMqUri, string cs,
        Action<IServiceCollection>? overrides) : WebApplicationFactory<TProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(Path.GetDirectoryName(typeof(TProgram).Assembly.Location)!);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = cs,
                ["Messaging:RabbitMq:Uri"] = rabbitMqUri,
                ["Auth:Mode"] = "DevHeaders"
            }));
            builder.ConfigureTestServices(services => overrides?.Invoke(services));
        }
    }
}
