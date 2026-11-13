using Ledgerline.BankSimulator.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ledgerline.BankSimulator.Infrastructure;

public sealed class BankDbContext(DbContextOptions<BankDbContext> options) : DbContext(options)
{
    public DbSet<BankOperation> Operations => Set<BankOperation>();
    public DbSet<BankAuthorization> Authorizations => Set<BankAuthorization>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var authorization = modelBuilder.Entity<BankAuthorization>();
        authorization.ToTable("bank_authorizations");
        authorization.HasKey(x => x.Id);
        authorization.Property(x => x.Id).HasColumnName("id");
        authorization.Property(x => x.Reference).HasColumnName("reference");
        authorization.Property(x => x.AmountMinor).HasColumnName("amount_minor");
        authorization.Property(x => x.Currency).HasColumnName("currency").HasColumnType("character(3)");
        authorization.Property(x => x.CardToken).HasColumnName("card_token");
        authorization.Property(x => x.Status).HasColumnName("status");
        authorization.Property(x => x.CaptureId).HasColumnName("capture_id");
        authorization.Property(x => x.CreatedAt).HasColumnName("created_at");
        MapOperations(modelBuilder);
    }

    private static void MapOperations(ModelBuilder modelBuilder)
    {
        var operation = modelBuilder.Entity<BankOperation>();
        operation.ToTable("bank_operations");
        operation.HasKey(x => x.IdempotencyKey);
        operation.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
        operation.Property(x => x.Kind).HasColumnName("kind");
        operation.Property(x => x.AuthorizationId).HasColumnName("authorization_id");
        operation.Property(x => x.CaptureId).HasColumnName("capture_id");
        operation.Property(x => x.RefundId).HasColumnName("refund_id");
        operation.Property(x => x.Reference).HasColumnName("reference");
        operation.Property(x => x.AmountMinor).HasColumnName("amount_minor");
        operation.Property(x => x.Currency).HasColumnName("currency").HasColumnType("character(3)");
        operation.Property(x => x.HttpStatus).HasColumnName("http_status");
        operation.Property(x => x.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb");
        operation.Property(x => x.OccurredAt).HasColumnName("occurred_at");
    }
}

public sealed class BankDesignTimeFactory : IDesignTimeDbContextFactory<BankDbContext>
{
    public BankDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<BankDbContext>()
        .UseNpgsql("Host=localhost;Database=banksim_db;Username=postgres").Options);
}
