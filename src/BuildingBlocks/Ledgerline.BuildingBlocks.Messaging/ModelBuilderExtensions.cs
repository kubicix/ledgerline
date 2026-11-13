using Ledgerline.BuildingBlocks.Messaging.Inbox;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.BuildingBlocks.Messaging;

public static class ModelBuilderExtensions
{
    public static ModelBuilder AddOutboxAndInbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        MapOutbox(modelBuilder);
        return modelBuilder.AddInbox();
    }

    public static ModelBuilder AddInbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var inbox = modelBuilder.Entity<InboxMessage>();
        inbox.ToTable("inbox_messages");
        inbox.HasKey(x => new { x.MessageId, x.Consumer });
        inbox.Property(x => x.MessageId).HasColumnName("message_id");
        inbox.Property(x => x.Consumer).HasColumnName("consumer").IsRequired();
        inbox.Property(x => x.Type).HasColumnName("type").IsRequired();
        inbox.Property(x => x.ReceivedAt).HasColumnName("received_at");
        return modelBuilder;
    }

    private static void MapOutbox(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<OutboxMessage>();
        outbox.ToTable("outbox_messages");
        outbox.HasKey(x => x.Sequence);
        outbox.Property(x => x.Sequence).HasColumnName("sequence").UseIdentityAlwaysColumn();
        outbox.Property(x => x.MessageId).HasColumnName("message_id");
        outbox.HasIndex(x => x.MessageId).IsUnique();
        outbox.Property(x => x.Type).HasColumnName("type").IsRequired();
        outbox.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        outbox.Property(x => x.CausationId).HasColumnName("causation_id");
        outbox.Property(x => x.TraceParent).HasColumnName("trace_parent");
        outbox.Property(x => x.Transport).HasColumnName("transport").HasDefaultValue("rabbitmq").IsRequired();
        outbox.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        outbox.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        outbox.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        outbox.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasDefaultValueSql("now()");
        outbox.Property(x => x.LastError).HasColumnName("last_error");
        outbox.HasIndex(x => x.Sequence).HasDatabaseName("ix_outbox_pending").HasFilter("processed_at IS NULL");
    }
}
