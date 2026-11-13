using DotNet.Testcontainers.Builders;
using System.Collections.Concurrent;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using System.Net;
using System.Net.Sockets;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Ledgerline.Testing;

public sealed class InfrastructureFixture : IAsyncLifetime
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> rabbitVirtualHosts = new(StringComparer.Ordinal);
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.7-alpine").Build();
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4.4-management")
        .WithPortBinding(FreePort(), 5672)
        .WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(new AmqpReady()))
        .Build();
    private readonly RedisContainer redis = new RedisBuilder("redis:8.8-alpine").Build();

    public string PostgresAdminConnectionString => postgres.GetConnectionString();
    public string RabbitMqUri => rabbit.GetConnectionString();
    public string RedisConnectionString => redis.GetConnectionString();

    public async ValueTask InitializeAsync() =>
        await Task.WhenAll(postgres.StartAsync(), rabbit.StartAsync(), redis.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), rabbit.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask());
    }

    public Task StopRabbitMqAsync() => rabbit.StopAsync();
    public Task StartRabbitMqAsync() => rabbit.StartAsync();
    public Task StopRedisAsync() => redis.StopAsync();
    public Task StartRedisAsync() => redis.StartAsync();

    public Task<string> CreateRabbitMqUriAsync(string databaseName)
    {
        if (string.IsNullOrEmpty(databaseName) || databaseName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Database name must contain ASCII letters, digits or underscores.", nameof(databaseName));
        return rabbitVirtualHosts.GetOrAdd(databaseName,
            name => new Lazy<Task<string>>(() => CreateVirtualHostAsync(name))).Value;
    }

    private async Task<string> CreateVirtualHostAsync(string databaseName)
    {
        var vhost = "ledgerline_" + databaseName;
        var created = await rabbit.ExecAsync(["rabbitmqctl", "add_vhost", vhost]);
        if (created.ExitCode != 0) throw new InvalidOperationException("Could not create the isolated RabbitMQ test virtual host.");
        var username = new ConnectionFactory { Uri = new Uri(RabbitMqUri) }.UserName;
        var permissions = await rabbit.ExecAsync(["rabbitmqctl", "set_permissions", "-p", vhost, username, ".*", ".*", ".*"]);
        if (permissions.ExitCode != 0) throw new InvalidOperationException("Could not configure the isolated RabbitMQ test virtual host.");
        return new UriBuilder(RabbitMqUri) { Path = vhost }.Uri.AbsoluteUri;
    }

    public async Task<string> CreateDatabaseAsync(string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length > 20 || prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Database prefix must contain at most 20 ASCII letters, digits or underscores.", nameof(prefix));
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(PostgresAdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(PostgresAdminConnectionString) { Database = name }.ConnectionString;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class AmqpReady : IWaitUntil
    {
        public async Task<bool> UntilAsync(IContainer container)
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(((RabbitMqContainer)container).GetConnectionString()),
                RequestedConnectionTimeout = TimeSpan.FromSeconds(1),
                AutomaticRecoveryEnabled = false
            };
            try
            {
                await using var connection = await factory.CreateConnectionAsync();
                return connection.IsOpen;
            }
            catch (BrokerUnreachableException)
            {
                return false;
            }
        }
    }
}



