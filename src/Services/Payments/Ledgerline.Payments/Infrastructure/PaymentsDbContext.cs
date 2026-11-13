using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Payments.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ledgerline.Payments.Infrastructure;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<SagaStepRow> SagaSteps => Set<SagaStepRow>();
    public DbSet<SagaTimeout> SagaTimeouts => Set<SagaTimeout>();
    public DbSet<AccountRef> AccountRefs => Set<AccountRef>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxAndInbox();
        MapPayments(modelBuilder);
        MapRefunds(modelBuilder);
        MapSaga(modelBuilder);
        MapAccounts(modelBuilder);
    }

    private static void MapPayments(ModelBuilder modelBuilder)
    {
        var payment = modelBuilder.Entity<Payment>();
        payment.ToTable("payments", table =>
        {
            table.HasCheckConstraint("ck_payment_amount", "amount_minor > 0");
            table.HasCheckConstraint("ck_captured", "captured_minor <= amount_minor");
            table.HasCheckConstraint("ck_refunded", "refunded_minor <= captured_minor");
        });
        payment.HasKey(x => x.Id);
        payment.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        payment.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>();
        payment.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
        payment.Property(x => x.SagaStep).HasColumnName("saga_step").HasConversion<string>();
        payment.Property(x => x.StepAttempt).HasColumnName("step_attempt").HasDefaultValue(1);
        payment.Property(x => x.CustomerId).HasColumnName("customer_id");
        payment.Property(x => x.SourceAccountId).HasColumnName("source_account_id");
        payment.Property(x => x.DestinationAccountId).HasColumnName("destination_account_id");
        payment.Property(x => x.AmountMinor).HasColumnName("amount_minor");
        payment.Property(x => x.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
        payment.Property(x => x.CaptureMode).HasColumnName("capture_mode").HasConversion<string>();
        payment.Property(x => x.CaptureRequestedMinor).HasColumnName("capture_requested_minor");
        payment.Property(x => x.CapturedMinor).HasColumnName("captured_minor").HasDefaultValue(0L);
        payment.Property(x => x.RefundedMinor).HasColumnName("refunded_minor").HasDefaultValue(0L);
        payment.Property(x => x.CardToken).HasColumnName("card_token");
        payment.Property(x => x.BankAuthorizationId).HasColumnName("bank_authorization_id");
        payment.Property(x => x.BankCaptureId).HasColumnName("bank_capture_id");
        payment.Property(x => x.FailureReason).HasColumnName("failure_reason");
        payment.Property(x => x.AuthorizationExpiresAt).HasColumnName("authorization_expires_at");
        payment.Property(x => x.ReconciledAt).HasColumnName("reconciled_at");
        payment.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        payment.Property(x => x.RequestHash).HasColumnName("request_hash").IsRequired();
        payment.Property(x => x.CreatedAt).HasColumnName("created_at");
        payment.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        payment.Property(x => x.Version).HasColumnName("xmin").IsRowVersion();
        payment.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique().HasDatabaseName("uq_payment_idem");
        payment.HasIndex(x => new { x.SourceAccountId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_payments_source");
        payment.HasIndex(x => new { x.DestinationAccountId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_payments_destination");
    }

    private static void MapRefunds(ModelBuilder modelBuilder)
    {
        var refund = modelBuilder.Entity<Refund>();
        refund.ToTable("refunds", table => table.HasCheckConstraint("ck_refund_amount", "amount_minor > 0"));
        refund.HasKey(x => x.Id);
        refund.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        refund.Property(x => x.PaymentId).HasColumnName("payment_id");
        refund.Property(x => x.AmountMinor).HasColumnName("amount_minor");
        refund.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
        refund.Property(x => x.FailureReason).HasColumnName("failure_reason");
        refund.Property(x => x.RequestedBy).HasColumnName("requested_by");
        refund.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        refund.Property(x => x.RequestHash).HasColumnName("request_hash").IsRequired();
        refund.Property(x => x.CreatedAt).HasColumnName("created_at");
        refund.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        refund.HasIndex(x => new { x.RequestedBy, x.IdempotencyKey }).IsUnique().HasDatabaseName("uq_refund_idem");
    }

    private static void MapSaga(ModelBuilder modelBuilder)
    {
        var step = modelBuilder.Entity<SagaStepRow>();
        step.ToTable("saga_steps");
        step.HasKey(x => x.Id);
        step.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        step.Property(x => x.PaymentId).HasColumnName("payment_id");
        step.Property(x => x.Step).HasColumnName("step").HasConversion<string>();
        step.Property(x => x.Outcome).HasColumnName("outcome").IsRequired();
        step.Property(x => x.Detail).HasColumnName("detail");
        step.Property(x => x.At).HasColumnName("at");
        step.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        step.HasIndex(x => new { x.PaymentId, x.Id }).HasDatabaseName("ix_saga_steps_payment");
        var timeout = modelBuilder.Entity<SagaTimeout>();
        timeout.ToTable("saga_timeouts");
        timeout.HasKey(x => x.Id);
        timeout.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        timeout.Property(x => x.PaymentId).HasColumnName("payment_id");
        timeout.Property(x => x.Step).HasColumnName("step").HasConversion<string>();
        timeout.Property(x => x.DueAt).HasColumnName("due_at");
        timeout.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        timeout.HasIndex(x => x.DueAt).HasDatabaseName("ix_saga_timeouts_due");
    }

    private static void MapAccounts(ModelBuilder modelBuilder)
    {
        var account = modelBuilder.Entity<AccountRef>();
        account.ToTable("account_refs");
        account.HasKey(x => x.AccountId);
        account.Property(x => x.AccountId).HasColumnName("account_id").ValueGeneratedNever();
        account.Property(x => x.OwnerId).HasColumnName("owner_id");
        account.Property(x => x.Type).HasColumnName("type").IsRequired();
        account.Property(x => x.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
        account.Property(x => x.Status).HasColumnName("status").IsRequired();
    }
}

public sealed class PaymentsDesignTimeFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PaymentsDbContext>()
        .UseNpgsql("Host=localhost;Database=payments_db;Username=postgres").Options);
}
