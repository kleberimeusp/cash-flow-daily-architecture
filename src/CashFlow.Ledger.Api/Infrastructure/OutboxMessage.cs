using System.Text.Json;
using CashFlow.Shared.Events;

namespace CashFlow.Ledger.Api.Infrastructure;

/// <summary>
/// Mensagem do Transactional Outbox (ADR-001/ADR-002). Gravada na mesma
/// transação do agregado; o <see cref="OutboxRelay"/> publica os registros com
/// <see cref="PublishedAt"/> nulo e marca após publicar no broker.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public string CorrelationId { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public static OutboxMessage FromEvent(EntryRegisteredEvent evt) => new()
    {
        Id = evt.EventId,
        Type = nameof(EntryRegisteredEvent),
        Payload = JsonSerializer.Serialize(evt),
        CorrelationId = evt.CorrelationId,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
