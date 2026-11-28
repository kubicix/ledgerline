using System.Net;
using System.Reflection;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Payments;
using Ledgerline.Reconciliation.Staging;
using Ledgerline.Testing;
using Npgsql;

namespace Ledgerline.Reconciliation.IntegrationTests;

/// <summary>The staging loop's failure rules of ruling S2: poison records are skipped, and Kafka is no readiness dependency.</summary>
public sealed class StagingResilienceTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Poison_record_is_skipped_and_the_partition_moves_on()
    {
        await using var host = await StagingHost.StartAsync(infrastructure);
        var payment = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var poison = await host.ProduceAsync(host.Options.PaymentEventsTopic, payment, "not an envelope"u8.ToArray());
        var envelope = new MessageEnvelope(Guid.NewGuid(), typeof(PaymentAuthorized).GetCustomAttribute<MessageTypeAttribute>()!.Name,
            DateTimeOffset.UtcNow, Guid.NewGuid(), null, null,
            EnvelopeSerializer.ToPayload(new PaymentAuthorized(payment, "TopUp", Guid.NewGuid(), 100, "TRY", null, Guid.NewGuid(), at, at.AddDays(7))));
        // Same key, so the same partition, right behind the poison record.
        var valid = await host.ProduceAsync(host.Options.PaymentEventsTopic, payment, EnvelopeSerializer.Serialize(envelope));
        valid.TopicPartition.ShouldBe(poison.TopicPartition);

        await host.WaitForCommitAsync([valid]);

        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT status, last_message_id FROM staged_payments", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        reader.GetString(0).ShouldBe("Authorized");
        reader.GetGuid(1).ShouldBe(envelope.MessageId);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Database_failure_does_not_commit_offsets_and_record_is_staged_after_recovery()
    {
        using var metrics = MetricRecorder.ForMeter("Ledgerline.Reconciliation");
        // An empty database: every staging statement fails with 42P01 (undefined table) until migrate runs.
        await using var host = await StagingHost.StartAsync(infrastructure, migrated: false);
        var payment = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var envelope = new MessageEnvelope(Guid.NewGuid(), typeof(PaymentAuthorized).GetCustomAttribute<MessageTypeAttribute>()!.Name,
            DateTimeOffset.UtcNow, Guid.NewGuid(), null, null,
            EnvelopeSerializer.ToPayload(new PaymentAuthorized(payment, "TopUp", Guid.NewGuid(), 100, "TRY", null, Guid.NewGuid(), at, at.AddDays(7))));
        var record = await host.ProduceAsync(host.Options.PaymentEventsTopic, payment, EnvelopeSerializer.Serialize(envelope));

        // The same record failed more than once, and more than the poison limit: it was consumed again, never skipped.
        await Eventually.ShouldAsync(() => Task.FromResult(metrics.Of("ledgerline.reconciliation.staging.retries",
                m => (string?)m.Tag("topic") == record.Topic).Length > StagingConsumerLoop.MaxAttempts),
            TimeSpan.FromSeconds(60), "The staging loop did not retry the record.");
        host.Progress.LastCommitted(record.TopicPartition).ShouldBeNull();
        (await host.CommittedOffsetAsync(record.TopicPartition)).ShouldBeNull();
        metrics.Of("ledgerline.reconciliation.staging.poison", m => (string?)m.Tag("topic") == record.Topic).ShouldBeEmpty();

        await Infrastructure.MigrateCommand.RunAsync(host.ConnectionString, TestContext.Current.CancellationToken);
        await host.WaitForCommitAsync([record]);

        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT status, last_message_id FROM staged_payments", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        reader.GetString(0).ShouldBe("Authorized");
        reader.GetGuid(1).ShouldBe(envelope.MessageId);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Service_is_ready_while_Kafka_is_unreachable()
    {
        await using var host = await ServiceHost<Program>.StartAsync(infrastructure, "recon_nokafka", settings: new Dictionary<string, string?>
        {
            // Nothing listens on the discard port: every connection attempt is refused.
            [StagingOptions.BootstrapServersKey] = "127.0.0.1:9"
        });
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
