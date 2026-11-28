using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Ledgerline.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

public sealed class Phase2FixtureTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Kafka_accepts_produce_and_consume()
    {
        var topic = $"fixture-{Guid.NewGuid():N}";
        var bootstrap = infrastructure.KafkaBootstrapServers;
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrap }).Build())
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
        }

        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = bootstrap,
            EnableIdempotence = true,
            Acks = Acks.All
        }).Build();
        var delivery = await producer.ProduceAsync(topic, new Message<string, string> { Key = "k1", Value = "v1" }, TestContext.Current.CancellationToken);
        delivery.Status.ShouldBe(PersistenceStatus.Persisted);

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = $"fixture-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
        consumer.Assign(new TopicPartitionOffset(topic, 0, Offset.Beginning));
        ConsumeResult<string, string>? received = null;
        await Eventually.ShouldAsync(() =>
        {
            received = consumer.Consume(TimeSpan.FromMilliseconds(500));
            return Task.FromResult(received is not null);
        }, TimeSpan.FromSeconds(30));
        received!.Message.Key.ShouldBe("k1");
        received.Message.Value.ShouldBe("v1");
    }

    [Fact]
    public async Task Mongo_accepts_insert_and_find()
    {
        var client = new MongoClient(infrastructure.MongoConnectionString);
        var collection = client.GetDatabase($"fixture_{Guid.NewGuid():N}").GetCollection<BsonDocument>("probe");
        await collection.InsertOneAsync(new BsonDocument { { "_id", "doc-1" }, { "amountMinor", 1250L } }, cancellationToken: TestContext.Current.CancellationToken);

        var found = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", "doc-1")).SingleAsync(TestContext.Current.CancellationToken);
        found["amountMinor"].AsInt64.ShouldBe(1250L);
    }
}
