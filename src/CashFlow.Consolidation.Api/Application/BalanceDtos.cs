using CashFlow.Consolidation.Api.Domain;

namespace CashFlow.Consolidation.Api.Application;

public sealed record BalanceResponse(
    Guid MerchantId,
    DateOnly Date,
    decimal TotalCredits,
    decimal TotalDebits,
    decimal Balance,
    int EntryCount,
    DateTimeOffset? UpdatedAt)
{
    public static BalanceResponse From(DailyBalance b) => new(
        b.MerchantId, b.Date, b.TotalCredits, b.TotalDebits, b.Balance, b.EntryCount, b.UpdatedAt);

    /// <summary>Dia sem movimentação retorna zeros com 200 — ausência de lançamento é fato de negócio válido.</summary>
    public static BalanceResponse Empty(Guid merchantId, DateOnly date) => new(
        merchantId, date, 0m, 0m, 0m, 0, null);
}
