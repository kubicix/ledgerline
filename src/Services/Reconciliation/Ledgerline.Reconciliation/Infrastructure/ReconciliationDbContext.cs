using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Reconciliation.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ledgerline.Reconciliation.Infrastructure;

/// <summary>
/// The tables of reconciliation.md section 6 plus <c>outbox_messages</c> and <c>inbox_messages</c>. The Quartz job store tables are created by
/// the Initial migration from the official script and are not part of the model.
/// </summary>
public sealed class ReconciliationDbContext(DbContextOptions<ReconciliationDbContext> options) : DbContext(options)
{
    public DbSet<StagedPaymentRow> StagedPayments => Set<StagedPaymentRow>();
    public DbSet<StagedLedgerLineRow> StagedLedgerLines => Set<StagedLedgerLineRow>();
    public DbSet<ReconciliationRun> Runs => Set<ReconciliationRun>();
    public DbSet<ReconciliationItem> Items => Set<ReconciliationItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Both messaging tables, as in every service database that uses messaging (building-blocks.md section 6).
        modelBuilder.AddOutboxAndInbox();
        MapStagedPayments(modelBuilder);
        MapStagedLedgerLines(modelBuilder);
        MapRuns(modelBuilder);
        MapItems(modelBuilder);
    }

    private static void MapStagedPayments(ModelBuilder modelBuilder)
    {
        var row = modelBuilder.Entity<StagedPaymentRow>();
        row.ToTable("staged_payments");
        row.HasKey(x => x.PaymentId);
        row.Property(x => x.PaymentId).HasColumnName("payment_id").ValueGeneratedNever();
        row.Property(x => x.Kind).HasColumnName("kind");
        row.Property(x => x.Status).HasColumnName("status");
        row.Property(x => x.AmountMinor).HasColumnName("amount_minor");
        row.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
        row.Property(x => x.BankCaptureId).HasColumnName("bank_capture_id");
        row.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        row.Property(x => x.LastMessageId).HasColumnName("last_message_id");
    }

    private static void MapStagedLedgerLines(ModelBuilder modelBuilder)
    {
        var row = modelBuilder.Entity<StagedLedgerLineRow>();
        row.ToTable("staged_ledger_lines");
        row.HasKey(x => new { x.JournalEntryId, x.LineNo });
        row.Property(x => x.JournalEntryId).HasColumnName("journal_entry_id");
        row.Property(x => x.LineNo).HasColumnName("line_no").ValueGeneratedNever();
        row.Property(x => x.AccountId).HasColumnName("account_id");
        row.Property(x => x.IsBankClearing).HasColumnName("is_bank_clearing");
        row.Property(x => x.EntryType).HasColumnName("entry_type");
        row.Property(x => x.Direction).HasColumnName("direction");
        row.Property(x => x.AmountMinor).HasColumnName("amount_minor");
        row.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
        row.Property(x => x.PaymentId).HasColumnName("payment_id");
        row.Property(x => x.PostedAt).HasColumnName("posted_at");
    }

    private static void MapRuns(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<ReconciliationRun>();
        run.ToTable("reconciliation_runs");
        run.HasKey(x => x.Id);
        run.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        run.Property(x => x.BusinessDate).HasColumnName("business_date");
        run.HasIndex(x => x.BusinessDate).IsUnique().HasDatabaseName("uq_reconciliation_runs_business_date");
        run.Property(x => x.Status).HasColumnName("status");
        run.Property(x => x.Matched).HasColumnName("matched").HasDefaultValue(0);
        run.Property(x => x.Mismatched).HasColumnName("mismatched").HasDefaultValue(0);
        run.Property(x => x.InputsIncomplete).HasColumnName("inputs_incomplete").HasDefaultValue(false);
        run.Property(x => x.Error).HasColumnName("error");
        run.Property(x => x.StartedAt).HasColumnName("started_at");
        run.Property(x => x.CompletedAt).HasColumnName("completed_at");
    }

    private static void MapItems(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<ReconciliationItem>();
        item.ToTable("reconciliation_items");
        item.HasKey(x => x.Id);
        item.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        item.Property(x => x.RunId).HasColumnName("run_id");
        item.HasOne<ReconciliationRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_reconciliation_items_run");
        item.HasIndex(x => x.RunId).HasDatabaseName("ix_reconciliation_items_run");
        item.Property(x => x.Code).HasColumnName("code");
        item.Property(x => x.Reference).HasColumnName("reference");
        item.Property(x => x.BankAmountMinor).HasColumnName("bank_amount_minor");
        item.Property(x => x.LedgerAmountMinor).HasColumnName("ledger_amount_minor");
        item.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
    }
}

public sealed class ReconciliationDesignTimeFactory : IDesignTimeDbContextFactory<ReconciliationDbContext>
{
    public ReconciliationDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ReconciliationDbContext>()
        .UseNpgsql("Host=localhost;Database=reconciliation_db;Username=postgres").Options);
}
