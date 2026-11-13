using Ledgerline.Testing;
using Npgsql;
using RabbitMQ.Client;
using StackExchange.Redis;

[assembly: AssemblyFixture(typeof(InfrastructureFixture))]

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

[Collection("Broker lifecycle")]
public sealed class InfrastructureFixtureTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Postgres_accepts_connections()
    {
        var connectionString = await infrastructure.CreateDatabaseAsync("fixture");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        connectionString.ShouldNotBe(infrastructure.PostgresAdminConnectionString);
    }

    [Fact]
    public async Task RabbitMq_accepts_connections()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(infrastructure.RabbitMqUri) }.CreateConnectionAsync(cancellationToken: TestContext.Current.CancellationToken);
        connection.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task Redis_answers_ping()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(infrastructure.RedisConnectionString);
        var response = await connection.GetDatabase().ExecuteAsync("PING");
        response.ToString().ShouldBe("PONG");
    }

    [Fact]
    public async Task Rabbit_can_be_stopped_and_started()
    {
        var uri = infrastructure.RabbitMqUri;
        await infrastructure.StopRabbitMqAsync();
        try
        {
            await Should.ThrowAsync<RabbitMQ.Client.Exceptions.BrokerUnreachableException>(async () =>
            {
                await using var unavailable = await new ConnectionFactory { Uri = new Uri(uri), RequestedConnectionTimeout = TimeSpan.FromSeconds(1) }.CreateConnectionAsync(cancellationToken: TestContext.Current.CancellationToken);
            });
        }
        finally
        {
            await infrastructure.StartRabbitMqAsync();
        }
        infrastructure.RabbitMqUri.ShouldBe(uri);
        await using var connection = await new ConnectionFactory { Uri = new Uri(uri) }.CreateConnectionAsync(cancellationToken: TestContext.Current.CancellationToken);
        connection.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task Eventually_times_out_with_message()
    {
        var error = await Assert.ThrowsAsync<ShouldAssertException>(() =>
            Eventually.ShouldAsync(() => Task.FromResult(false), TimeSpan.FromMilliseconds(100), "expected readiness"));
        error.Message.ShouldContain("expected readiness");
    }
}



[CollectionDefinition("Broker lifecycle", DisableParallelization = true)]
public sealed class BrokerLifecycleDefinition { }

