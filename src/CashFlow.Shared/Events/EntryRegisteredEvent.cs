namespace CashFlow.Shared.Events;

/// <summary>
/// Evento publicado pelo Ledger (via Outbox) quando um lançamento é registrado.
/// <c>EventId</c> é a chave de idempotência do consumidor (ADR-005, camada 2).
/// </summary>
public sealed record EntryRegisteredEvent(
    Guid EventId,
    Guid EntryId,
    Guid MerchantId,
    decimal Amount,
    DateOnly OccurredOn,
    string Description,
    string CorrelationId,
    DateTimeOffset RegisteredAt);
