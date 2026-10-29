namespace CashFlow.Consolidation.Api.Domain;

/// <summary>
/// Saldo diário consolidado de um comerciante. Projeção derivada do Ledger —
/// nunca é fonte da verdade e é integralmente reconstruível (ADR-001).
/// Chave composta: (MerchantId, Date) — a data é a de competência (RN-02).
/// </summary>
public sealed class DailyBalance
{
    public Guid MerchantId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal TotalCredits { get; private set; }
    public decimal TotalDebits { get; private set; }
    public decimal Balance { get; private set; }
    public int EntryCount { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private DailyBalance() { } // EF Core

    public DailyBalance(Guid merchantId, DateOnly date, decimal firstAmount, DateTimeOffset now)
    {
        MerchantId = merchantId;
        Date = date;
        Apply(firstAmount, now);
    }

    /// <summary>RN-04: arredondamento ToEven em 2 casas em cada aplicação.</summary>
    public void Apply(decimal amount, DateTimeOffset now)
    {
        var rounded = Math.Round(amount, 2, MidpointRounding.ToEven);
        if (rounded > 0)
            TotalCredits += rounded;
        else if (rounded < 0)
            TotalDebits += -rounded;

        Balance = Math.Round(Balance + rounded, 2, MidpointRounding.ToEven);
        EntryCount++;
        UpdatedAt = now;
    }
}
