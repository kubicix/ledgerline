using Microsoft.EntityFrameworkCore;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests.Support;

public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>().HasKey(w => w.Id);
        modelBuilder.AddOutboxAndInbox();
    }
}

public sealed record Widget(Guid Id, string Name);
