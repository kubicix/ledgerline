using System.Diagnostics;
using Ledgerline.BuildingBlocks.Messaging.IntegrationTests.Support;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

public sealed class OutboxTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Enqueue_then_SaveChanges_writes_one_pending_row_with_type_and_payload()
    {
        await using var db = await CreateDbAsync();
        var correlation = Guid.NewGuid();
        var causation = Guid.NewGuid();
        var outbox = new EfOutbox<TestDbContext>(db, new MessageTypeRegistry(), new SystemClock());
        outbox.Enqueue(new WidgetRenamed("new name"), correlation, causation);
        (await db.Set<OutboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var row = await db.Set<OutboxMessage>().SingleAsync(TestContext.Current.CancellationToken);
        row.Type.ShouldBe("accounts.event.widget-renamed.v1");
        EnvelopeSerializer.FromPayload(row.Payload, typeof(WidgetRenamed)).ShouldBe(new WidgetRenamed("new name"));
        row.CorrelationId.ShouldBe(correlation);
        row.CausationId.ShouldBe(causation);
        row.MessageId.Version.ShouldBe(7);
        row.Sequence.ShouldBeGreaterThan(0);
        row.ProcessedAt.ShouldBeNull();
        row.Attempts.ShouldBe(0);
        row.Transport.ShouldBe("rabbitmq");
        row.OccurredAt.ShouldBeInRange(DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Enqueue_then_rollback_writes_nothing()
    {
        await using var db = await CreateDbAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            db.Widgets.Add(new Widget(Guid.NewGuid(), "test"));
            new EfOutbox<TestDbContext>(db, new MessageTypeRegistry(), new SystemClock())
                .Enqueue(new WidgetRenamed("rolled back"), Guid.NewGuid());
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }
        (await db.Widgets.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.Set<OutboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Enqueue_captures_current_trace_parent()
    {
        await using var db = await CreateDbAsync();
        using var activity = new Activity("enqueue").SetIdFormat(ActivityIdFormat.W3C).Start();
        new EfOutbox<TestDbContext>(db, new MessageTypeRegistry(), new SystemClock())
            .Enqueue(new WidgetRenamed("traced"), Guid.NewGuid());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await db.Set<OutboxMessage>().SingleAsync(TestContext.Current.CancellationToken)).TraceParent.ShouldBe(activity.Id);
    }

    [Fact]
    public async Task Schema_matches_spec()
    {
        await using var db = await CreateDbAsync();
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var (table, columns) in new Dictionary<string, string[]>
        {
            ["outbox_messages"] = ["sequence", "message_id", "type", "payload", "correlation_id", "causation_id", "trace_parent", "transport", "occurred_at", "processed_at", "attempts", "next_attempt_at", "last_error"],
            ["inbox_messages"] = ["message_id", "consumer", "type", "received_at"]
        })
        {
            await using var command = new NpgsqlCommand("SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table", connection);
            command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            var actual = new List<string>();
            while (await reader.ReadAsync(TestContext.Current.CancellationToken)) actual.Add(reader.GetString(0));
            actual.Order().ShouldBe(columns.Order());
        }
    }

    private async Task<TestDbContext> CreateDbAsync()
    {
        var connectionString = await infrastructure.CreateDatabaseAsync("outbox");
        var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return db;
    }

    [MessageType("accounts.event.widget-renamed.v1")]
    public sealed record WidgetRenamed(string Name);
}
