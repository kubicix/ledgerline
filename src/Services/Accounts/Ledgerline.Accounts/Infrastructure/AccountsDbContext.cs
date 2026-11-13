using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ledgerline.Accounts.Infrastructure;

public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<LedgerEvent> Events => Set<LedgerEvent>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<Hold> Holds => Set<Hold>();
    public DbSet<LedgerOperation> LedgerOperations => Set<LedgerOperation>();
    public DbSet<AccountStatusRequest> StatusRequests => Set<AccountStatusRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxAndInbox();
        MapAccounts(modelBuilder);
        MapEvents(modelBuilder);
        MapJournal(modelBuilder);
        MapHoldsAndOperations(modelBuilder);
        var request = modelBuilder.Entity<AccountStatusRequest>();
        request.ToTable("account_status_requests");
        request.HasKey(x => new { x.CallerId, x.Key });
        request.Property(x => x.CallerId).HasColumnName("caller_id");
        request.Property(x => x.Key).HasColumnName("idempotency_key");
        request.Property(x => x.RequestHash).HasColumnName("request_hash");
        request.Property(x => x.AccountId).HasColumnName("account_id");
        request.Property(x => x.CreatedAt).HasColumnName("created_at");
    }

    private static void MapAccounts(ModelBuilder modelBuilder)
    {
        var account = modelBuilder.Entity<Account>();
        account.ToTable("accounts", table => table.HasCheckConstraint("ck_wallet_non_negative", "type = 'System' OR balance_minor >= 0"));
        account.HasKey(x => x.Id);
        account.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        account.Property(x => x.OwnerId).HasColumnName("owner_id");
        account.Property(x => x.Type).HasColumnName("type").IsRequired();
        account.Property(x => x.SystemCode).HasColumnName("system_code");
        account.Property(x => x.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
        account.Property(x => x.Status).HasColumnName("status").IsRequired();
        account.Property(x => x.BalanceMinor).HasColumnName("balance_minor").HasDefaultValue(0L);
        account.Property(x => x.Version).HasColumnName("version");
        account.Property(x => x.ContactEmail).HasColumnName("contact_email");
        account.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
        account.Property(x => x.RequestHash).HasColumnName("request_hash");
        account.Property(x => x.OpenedAt).HasColumnName("opened_at");
        account.HasIndex(x => new { x.OwnerId, x.Type, x.Currency }).IsUnique().HasDatabaseName("uq_owner_type_currency");
        account.HasIndex(x => new { x.SystemCode, x.Currency }).IsUnique().HasDatabaseName("uq_system");
        account.HasIndex(x => new { x.OwnerId, x.IdempotencyKey }).IsUnique().HasDatabaseName("uq_account_idem");
    }

    private static void MapEvents(ModelBuilder modelBuilder)
    {
        var events = modelBuilder.Entity<LedgerEvent>();
        events.ToTable("events");
        events.HasKey(x => x.GlobalSeq);
        events.Property(x => x.GlobalSeq).HasColumnName("global_seq").UseIdentityAlwaysColumn();
        events.Property(x => x.StreamId).HasColumnName("stream_id");
        events.Property(x => x.Version).HasColumnName("version");
        events.Property(x => x.Type).HasColumnName("type").IsRequired();
        events.Property(x => x.Data).HasColumnName("data").HasColumnType("jsonb");
        events.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        events.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        events.HasIndex(x => new { x.StreamId, x.Version }).IsUnique();
    }

    private static void MapJournal(ModelBuilder modelBuilder)
    {
        var entry = modelBuilder.Entity<JournalEntry>();
        entry.ToTable("journal_entries");
        entry.HasKey(x => x.Id);
        entry.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        entry.Property(x => x.EntryType).HasColumnName("entry_type").IsRequired();
        entry.Property(x => x.OperationId).HasColumnName("operation_id");
        entry.Property(x => x.PaymentId).HasColumnName("payment_id");
        entry.Property(x => x.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
        entry.Property(x => x.PostedAt).HasColumnName("posted_at");
        entry.HasIndex(x => x.OperationId).IsUnique();
        entry.HasIndex(x => x.PaymentId).HasDatabaseName("ix_journal_payment");
        var line = modelBuilder.Entity<JournalLine>();
        line.ToTable("journal_lines", table => table.HasCheckConstraint("ck_line_positive", "amount_minor > 0"));
        line.HasKey(x => new { x.JournalEntryId, x.LineNo });
        line.Property(x => x.JournalEntryId).HasColumnName("journal_entry_id");
        line.Property(x => x.LineNo).HasColumnName("line_no");
        line.Property(x => x.AccountId).HasColumnName("account_id");
        line.Property(x => x.Direction).HasColumnName("direction").IsRequired();
        line.Property(x => x.AmountMinor).HasColumnName("amount_minor");
        line.Property(x => x.BalanceAfterMinor).HasColumnName("balance_after_minor");
        line.Property(x => x.EventGlobalSeq).HasColumnName("event_global_seq");
        line.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.NoAction);
        line.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.NoAction);
        line.HasIndex(x => new { x.AccountId, x.EventGlobalSeq }).IsDescending(false, true).HasDatabaseName("ix_lines_account");
    }

    private static void MapHoldsAndOperations(ModelBuilder modelBuilder)
    {
        var hold = modelBuilder.Entity<Hold>();
        hold.ToTable("holds", table => table.HasCheckConstraint("ck_hold_positive", "amount_minor > 0"));
        hold.HasKey(x => x.PaymentId);
        hold.Property(x => x.PaymentId).HasColumnName("payment_id").ValueGeneratedNever();
        hold.Property(x => x.AccountId).HasColumnName("account_id");
        hold.Property(x => x.AmountMinor).HasColumnName("amount_minor");
        hold.Property(x => x.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
        hold.Property(x => x.Status).HasColumnName("status").IsRequired();
        hold.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.NoAction);
        var operation = modelBuilder.Entity<LedgerOperation>();
        operation.ToTable("ledger_operations");
        operation.HasKey(x => x.OperationId);
        operation.Property(x => x.OperationId).HasColumnName("operation_id").ValueGeneratedNever();
        operation.Property(x => x.Kind).HasColumnName("kind").IsRequired();
        operation.Property(x => x.PaymentId).HasColumnName("payment_id");
        operation.Property(x => x.Outcome).HasColumnName("outcome").IsRequired();
        operation.Property(x => x.ReasonCode).HasColumnName("reason_code");
        operation.Property(x => x.JournalEntryId).HasColumnName("journal_entry_id");
        operation.Property(x => x.CompletedAt).HasColumnName("completed_at");
    }
}

public sealed class AccountsDesignTimeFactory : IDesignTimeDbContextFactory<AccountsDbContext>
{
    public AccountsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql("Host=localhost;Database=accounts_db;Username=postgres").Options);
}
