using DotNet.Testcontainers.Builders;
using System.Collections.Concurrent;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using System.Net;
using System.Net.Sockets;
using Npgsql;
using Testcontainers.Kafka;
using Testcontainers.MongoDb;
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
    // Phase 2 containers start on first use only: most integration modules never touch Kafka or MongoDB.
    private readonly Lazy<(KafkaContainer Container, string BootstrapServers)> kafka;
    private readonly Lazy<(MongoDbContainer Container, string ConnectionString)> mongo;

    public InfrastructureFixture()
    {
        kafka = new(StartKafka);
        mongo = new(StartMongo);
    }

    public string PostgresAdminConnectionString => postgres.GetConnectionString();
    public string RabbitMqUri => rabbit.GetConnectionString();
    public string RedisConnectionString => redis.GetConnectionString();
    public string KafkaBootstrapServers => kafka.Value.BootstrapServers;
    public string MongoConnectionString => mongo.Value.ConnectionString;

    public async ValueTask InitializeAsync() =>
        await Task.WhenAll(postgres.StartAsync(), rabbit.StartAsync(), redis.StartAsync());

    public async ValueTask DisposeAsync()
    {
        var disposals = new List<Task>
        {
            postgres.DisposeAsync().AsTask(), rabbit.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask()
        };
        if (kafka.IsValueCreated) disposals.Add(kafka.Value.Container.DisposeAsync().AsTask());
        if (mongo.IsValueCreated) disposals.Add(mongo.Value.Container.DisposeAsync().AsTask());
        await Task.WhenAll(disposals);
    }

    public Task StopRabbitMqAsync() => rabbit.StopAsync();
    public Task StartRabbitMqAsync() => rabbit.StartAsync();
    public Task StopRedisAsync() => redis.StopAsync();
    public Task StartRedisAsync() => redis.StartAsync();

    // Kafka keeps its host port across stop and start: the container is built with a fixed host port, and the
    // module rewrites the advertised listener on every start. Clients that reconnect to the bootstrap address
    // taken before the outage therefore find the broker again. A restart wipes nothing: the log directory is
    // part of the stopped container's filesystem.
    public async Task StopKafkaAsync() => await (await Task.Run(() => kafka.Value)).Container.StopAsync();
    public async Task StartKafkaAsync() => await (await Task.Run(() => kafka.Value)).Container.StartAsync();

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

    private static (KafkaContainer Container, string BootstrapServers) StartKafka()
    {
        var container = new KafkaBuilder("apache/kafka:4.4.0")
            .WithKRaft()
            .WithPortBinding(FreePort(), KafkaBuilder.KafkaPort)
            .WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(new KafkaReady()))
            .Build();
        StartBlocking(container);
        return (container, $"{container.Hostname}:{container.GetMappedPublicPort(KafkaBuilder.KafkaPort)}");
    }

    private static (MongoDbContainer Container, string ConnectionString) StartMongo()
    {
        var container = new MongoDbBuilder("mongo:9.0").Build();
        StartBlocking(container);
        return (container, container.GetConnectionString());
    }

    // The properties are synchronous, so the first access blocks while the container starts. Task.Run keeps the
    // wait off any synchronization context of the calling test.
    private static void StartBlocking(IContainer container)
    {
        try
        {
            Task.Run(() => container.StartAsync()).GetAwaiter().GetResult();
        }
        catch
        {
            Task.Run(() => container.DisposeAsync().AsTask()).GetAwaiter().GetResult();
            throw;
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class KafkaReady : IWaitUntil
    {
        // Asks the broker itself through its in-container listener. Unlike a log line it is also correct after a
        // restart, when the log still holds the lines of the previous run.
        public async Task<bool> UntilAsync(IContainer container) =>
            (await container.ExecAsync(["/opt/kafka/bin/kafka-broker-api-versions.sh", "--bootstrap-server", "localhost:9092"])).ExitCode == 0;
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



