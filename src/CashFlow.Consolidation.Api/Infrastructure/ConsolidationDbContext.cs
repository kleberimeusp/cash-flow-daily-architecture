using CashFlow.Consolidation.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Consolidation.Api.Infrastructure;

public sealed class ConsolidationDbContext(DbContextOptions<ConsolidationDbContext> options) : DbContext(options)
{
    public DbSet<DailyBalance> DailyBalances => Set<DailyBalance>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DailyBalance>(e =>
        {
            e.ToTable("daily_balances");
            e.HasKey(x => new { x.MerchantId, x.Date });
            e.Property(x => x.MerchantId).HasColumnName("merchant_id");
            e.Property(x => x.Date).HasColumnName("date");
            e.Property(x => x.TotalCredits).HasColumnName("total_credits").HasPrecision(18, 2);
            e.Property(x => x.TotalDebits).HasColumnName("total_debits").HasPrecision(18, 2);
            e.Property(x => x.Balance).HasColumnName("balance").HasPrecision(18, 2);
            e.Property(x => x.EntryCount).HasColumnName("entry_count");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        });
    }
}
