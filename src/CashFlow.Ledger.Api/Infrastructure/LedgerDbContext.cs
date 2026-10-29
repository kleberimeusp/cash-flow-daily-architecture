using Microsoft.EntityFrameworkCore;
using Entry = CashFlow.Ledger.Api.Domain.Entry;

namespace CashFlow.Ledger.Api.Infrastructure;

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Entry>(e =>
        {
            e.ToTable("entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.MerchantId).HasColumnName("merchant_id");
            e.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2);
            e.Property(x => x.OccurredOn).HasColumnName("occurred_on");
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(Domain.Entry.MaxDescriptionLength);
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            e.Property(x => x.ReversesEntryId).HasColumnName("reverses_entry_id");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            // ADR-005 camada 1: unicidade de (merchant, chave) — corrida vira 23505.
            e.HasIndex(x => new { x.MerchantId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.MerchantId, x.OccurredOn });
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(128);
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.PublishedAt).HasColumnName("published_at");

            // Índice parcial: o poll do relay só varre mensagens pendentes.
            e.HasIndex(x => x.CreatedAt)
                .HasDatabaseName("ix_outbox_messages_pending")
                .HasFilter("published_at IS NULL");
        });
    }
}
