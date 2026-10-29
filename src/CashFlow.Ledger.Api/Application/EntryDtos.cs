using CashFlow.Ledger.Api.Domain;

namespace CashFlow.Ledger.Api.Application;

public sealed record CreateEntryRequest(decimal Amount, DateOnly OccurredOn, string? Description);

public sealed record EntryResponse(
    Guid Id,
    Guid MerchantId,
    decimal Amount,
    DateOnly OccurredOn,
    string Description,
    Guid? ReversesEntryId,
    string IdempotencyKey,
    string CorrelationId,
    DateTimeOffset CreatedAt)
{
    public static EntryResponse From(Entry e) => new(
        e.Id, e.MerchantId, e.Amount, e.OccurredOn, e.Description,
        e.ReversesEntryId, e.IdempotencyKey, e.CorrelationId, e.CreatedAt);
}
