using CashFlow.Ledger.Api.Domain;
using CashFlow.Ledger.Api.Infrastructure;
using CashFlow.Shared.Events;
using CashFlow.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CashFlow.Ledger.Api.Application;

/// <summary>
/// Casos de uso do Ledger. A decisão central da ADR-001 está em
/// <see cref="PersistAsync"/>: agregado + evento de outbox na MESMA transação
/// local — não existe janela entre commit e publish.
/// </summary>
public sealed class EntryService(
    LedgerDbContext db,
    IMerchantContext merchantContext,
    LedgerMetrics metrics,
    TimeProvider timeProvider,
    ILogger<EntryService> logger)
{
    public async Task<CreateEntryResult> CreateAsync(
        CreateEntryRequest? request, string? idempotencyKey, string correlationId, CancellationToken ct)
    {
        // RN-05: Idempotency-Key obrigatório na criação.
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return new CreateEntryResult.Invalid(
                Result.Failure("idempotencyKey", "O header Idempotency-Key é obrigatório.").Errors);

        var merchantId = merchantContext.MerchantId;

        var existing = await FindByKeyAsync(merchantId, idempotencyKey, ct);
        if (existing is not null)
            return new CreateEntryResult.Duplicate(existing);

        var created = Entry.Create(
            merchantId, request?.Amount ?? 0m, request?.OccurredOn ?? default,
            request?.Description, idempotencyKey, correlationId, Today());
        if (created.IsFailure)
            return new CreateEntryResult.Invalid(created.Errors);

        return await PersistAsync(created.Value!, idempotencyKey, correlationId, ct,
            onDuplicate: e => new CreateEntryResult.Duplicate(e));
    }

    public async Task<Entry?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        // Filtro por tenant na query: lançamento de outro comerciante é 404 (anti-IDOR, RN-06).
        var merchantId = merchantContext.MerchantId;
        return await db.Entries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.MerchantId == merchantId, ct);
    }

    public async Task<IReadOnlyList<Entry>> ListAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var merchantId = merchantContext.MerchantId;
        return await db.Entries.AsNoTracking()
            .Where(e => e.MerchantId == merchantId && e.OccurredOn >= from && e.OccurredOn <= to)
            .OrderBy(e => e.OccurredOn)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<ReverseEntryResult> ReverseAsync(
        Guid id, string? idempotencyKey, string correlationId, CancellationToken ct)
    {
        var merchantId = merchantContext.MerchantId;

        var original = await db.Entries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.MerchantId == merchantId, ct);
        if (original is null)
            return new ReverseEntryResult.NotFound();
        if (original.ReversesEntryId is not null)
            return new ReverseEntryResult.Conflict(
                "Não é possível estornar um lançamento que já é um estorno.");

        // Chave natural: repetir o estorno do mesmo lançamento devolve o estorno original.
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? $"reverse:{id}" : idempotencyKey;

        var existing = await FindByKeyAsync(merchantId, key, ct);
        if (existing is not null)
            return new ReverseEntryResult.Duplicate(existing);

        var reversal = original.CreateReversal(key, correlationId, Today());
        if (reversal.IsFailure)
            return new ReverseEntryResult.Invalid(reversal.Errors);

        var persisted = await PersistAsync(reversal.Value!, key, correlationId, ct,
            onDuplicate: e => new CreateEntryResult.Duplicate(e));

        return persisted switch
        {
            CreateEntryResult.Created c => new ReverseEntryResult.Created(c.Entry),
            CreateEntryResult.Duplicate d => new ReverseEntryResult.Duplicate(d.Entry),
            CreateEntryResult.Invalid i => new ReverseEntryResult.Invalid(i.Errors),
            _ => throw new InvalidOperationException("Resultado inesperado ao persistir estorno.")
        };
    }

    private async Task<CreateEntryResult> PersistAsync(
        Entry entry, string idempotencyKey, string correlationId, CancellationToken ct,
        Func<Entry, CreateEntryResult> onDuplicate)
    {
        var @event = new EntryRegisteredEvent(
            EventId: Guid.NewGuid(),
            EntryId: entry.Id,
            MerchantId: entry.MerchantId,
            Amount: entry.Amount,
            OccurredOn: entry.OccurredOn,
            Description: entry.Description,
            CorrelationId: correlationId,
            RegisteredAt: entry.CreatedAt);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Entries.Add(entry);
        db.OutboxMessages.Add(OutboxMessage.FromEvent(@event));
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // ADR-005 camada 1: corrida entre requisições idênticas — quem perde
            // o INSERT captura 23505 e devolve o vencedor, sem duplicar.
            logger.LogInformation(
                "Corrida de idempotência na chave {IdempotencyKey}; devolvendo o vencedor", idempotencyKey);
            var winner = await FindByKeyAsync(entry.MerchantId, idempotencyKey, ct);
            return winner is not null
                ? onDuplicate(winner)
                : throw new InvalidOperationException("Violação de unicidade sem registro vencedor.", ex);
        }

        metrics.EntryRegistered(entry.MerchantId, Classify(entry));
        return new CreateEntryResult.Created(entry);
    }

    private Task<Entry?> FindByKeyAsync(Guid merchantId, string idempotencyKey, CancellationToken ct)
        => db.Entries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.MerchantId == merchantId && e.IdempotencyKey == idempotencyKey, ct);

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    private static string Classify(Entry e)
        => e.ReversesEntryId is not null ? "reversal" : e.Amount >= 0 ? "credit" : "debit";

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
