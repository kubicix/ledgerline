using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Notifications.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ledgerline.Notifications.Infrastructure;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<Recipient> Recipients => Set<Recipient>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddInbox();
        var recipient = modelBuilder.Entity<Recipient>();
        recipient.ToTable("recipients");
        recipient.HasKey(x => x.AccountId);
        recipient.Property(x => x.AccountId).HasColumnName("account_id").ValueGeneratedNever();
        recipient.Property(x => x.OwnerId).HasColumnName("owner_id");
        recipient.Property(x => x.Email).HasColumnName("email");
        recipient.HasIndex(x => x.OwnerId).HasDatabaseName("ix_recipients_owner");
        MapNotifications(modelBuilder);
    }

    private static void MapNotifications(ModelBuilder modelBuilder)
    {
        var row = modelBuilder.Entity<Notification>();
        row.ToTable("notifications");
        row.HasKey(x => x.Id);
        row.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        row.Property(x => x.SourceMessageId).HasColumnName("source_message_id");
        row.Property(x => x.RecipientOwnerId).HasColumnName("recipient_owner_id");
        row.Property(x => x.RecipientEmail).HasColumnName("recipient_email");
        row.Property(x => x.Template).HasColumnName("template");
        row.Property(x => x.PaymentId).HasColumnName("payment_id");
        row.Property(x => x.Subject).HasColumnName("subject");
        row.Property(x => x.Body).HasColumnName("body");
        row.Property(x => x.Status).HasColumnName("status");
        row.Property(x => x.Reason).HasColumnName("reason");
        row.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        row.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
        row.Property(x => x.CreatedAt).HasColumnName("created_at");
        row.Property(x => x.SentAt).HasColumnName("sent_at");
        row.HasIndex(x => new { x.SourceMessageId, x.RecipientOwnerId, x.Template }).IsUnique().HasDatabaseName("uq_notification");
        row.HasIndex(x => x.NextAttemptAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_notifications_pending");
        row.HasIndex(x => new { x.RecipientOwnerId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_notifications_owner");
    }
}

public sealed class NotificationsDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<NotificationsDbContext>()
        .UseNpgsql("Host=localhost;Database=notifications_db;Username=postgres").Options);
}
