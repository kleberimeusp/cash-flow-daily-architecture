namespace CashFlow.Consolidation.Api.Domain;

/// <summary>
/// Registro de evento já consumido (ADR-005, camada 2). <see cref="EventId"/> é
/// a PK: reentrega do broker vira violação de unicidade e é absorvida.
/// </summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }

    private ProcessedEvent() { } // EF Core

    public ProcessedEvent(Guid eventId, DateTimeOffset processedAt)
    {
        EventId = eventId;
        ProcessedAt = processedAt;
    }
}
