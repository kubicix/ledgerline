using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Risk.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ledgerline.Risk.Infrastructure;

public sealed class RiskDbContext(DbContextOptions<RiskDbContext> options) : DbContext(options)
{
    public DbSet<RiskRuleEntity> Rules => Set<RiskRuleEntity>();
    public DbSet<RiskDecision> Decisions => Set<RiskDecision>();
    public DbSet<DailyTotal> DailyTotals => Set<DailyTotal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxAndInbox();
        MapRules(modelBuilder);
        MapDecisions(modelBuilder);
        var total = modelBuilder.Entity<DailyTotal>();
        total.ToTable("daily_totals");
        total.HasKey(x => new { x.CustomerId, x.Day, x.Currency });
        total.Property(x => x.CustomerId).HasColumnName("customer_id");
        total.Property(x => x.Day).HasColumnName("day");
        total.Property(x => x.Currency).HasColumnName("currency").HasColumnType("character(3)");
        total.Property(x => x.TotalMinor).HasColumnName("total_minor").HasDefaultValue(0L);
    }

    private static void MapRules(ModelBuilder modelBuilder)
    {
        var rule = modelBuilder.Entity<RiskRuleEntity>();
        rule.ToTable("risk_rules");
        rule.HasKey(x => x.Id);
        rule.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        rule.Property(x => x.Code).HasColumnName("code").HasConversion<string>();
        rule.Property(x => x.Kind).HasColumnName("kind");
        rule.Property(x => x.Currency).HasColumnName("currency").HasColumnType("character(3)");
        rule.Property(x => x.ThresholdMinor).HasColumnName("threshold_minor");
        rule.Property(x => x.MaxCount).HasColumnName("max_count");
        rule.Property(x => x.WindowSeconds).HasColumnName("window_seconds");
        rule.Property(x => x.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        rule.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }

    private static void MapDecisions(ModelBuilder modelBuilder)
    {
        var decision = modelBuilder.Entity<RiskDecision>();
        decision.ToTable("risk_decisions");
        decision.HasKey(x => x.PaymentId);
        decision.Property(x => x.PaymentId).HasColumnName("payment_id").ValueGeneratedNever();
        decision.Property(x => x.CustomerId).HasColumnName("customer_id");
        decision.Property(x => x.Kind).HasColumnName("kind");
        decision.Property(x => x.AmountMinor).HasColumnName("amount_minor");
        decision.Property(x => x.Currency).HasColumnName("currency").HasColumnType("character(3)");
        decision.Property(x => x.Outcome).HasColumnName("outcome");
        decision.Property(x => x.RuleCodes).HasColumnName("rule_codes").HasColumnType("text[]");
        decision.Property(x => x.EvaluatedAt).HasColumnName("evaluated_at");
        decision.HasIndex(x => new { x.CustomerId, x.EvaluatedAt }).IsDescending(false, true).HasDatabaseName("ix_decisions_customer_time");
    }
}

public sealed class RiskDesignTimeFactory : IDesignTimeDbContextFactory<RiskDbContext>
{
    public RiskDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<RiskDbContext>()
        .UseNpgsql("Host=localhost;Database=risk_db;Username=postgres").Options);
}
