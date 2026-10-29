using CashFlow.Consolidation.Api.Infrastructure;
using CashFlow.Shared.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Npgsql;

namespace CashFlow.Consolidation.Api.Application;

/// <summary>
/// Consumidor idempotente de <see cref="EntryRegisteredEvent"/> (RF-10).
/// ADR-005 camada 2: <c>processed_events</c> com <c>event_id</c> como PK,
/// verificado e gravado na mesma transação do UPSERT — reentrega do broker
/// nunca consolida duas vezes.
/// </summary>
public sealed class EntryConsumer(
    ConsolidationDbContext db,
    DailyBalanceRepository repository,
    IDistributedCache cache,
    ConsolidationMetrics metrics,
    ILogger<EntryConsumer> logger) : IConsumer<EntryRegisteredEvent>
{
    public Task Consume(ConsumeContext<EntryRegisteredEvent> context)
        => Consume(context.Message, context.CancellationToken);

    public async Task Consume(EntryRegisteredEvent evt, CancellationToken ct = default)
    {
        if (await db.ProcessedEvents.AnyAsync(p => p.EventId == evt.EventId, ct))
        {
            logger.LogInformation("Evento {EventId} já processado; reentrega ignorada", evt.EventId);
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await repository.ApplyAsync(evt.MerchantId, evt.OccurredOn, evt.Amount, ct);
            db.ProcessedEvents.Add(new Domain.ProcessedEvent(evt.EventId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Corrida de reentrega: outro consumidor venceu o INSERT do event_id.
            logger.LogInformation("Evento {EventId} processado por outro consumidor; ignorando", evt.EventId);
            return;
        }

        // Invalida o cache do dia afetado; o TTL de 30s limita o staleness residual (ADR-006).
        try
        {
            await cache.RemoveAsync(BalanceQueryService.CacheKey(evt.MerchantId, evt.OccurredOn), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao invalidar cache do dia {Date}", evt.OccurredOn);
        }

        metrics.BalanceConsolidated(evt.MerchantId);
        metrics.ConsolidationLag((DateTimeOffset.UtcNow - evt.RegisteredAt).TotalSeconds);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
