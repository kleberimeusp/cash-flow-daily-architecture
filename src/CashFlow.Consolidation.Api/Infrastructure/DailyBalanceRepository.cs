using CashFlow.Consolidation.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Consolidation.Api.Infrastructure;

/// <summary>
/// Persistência do saldo diário. Em Postgres usa UPSERT atômico
/// (<c>ON CONFLICT DO UPDATE</c>) — decisão central do dimensionamento
/// (RNF-02/RNF-03): sem ele, 50 escritas concorrentes no mesmo dia viram
/// deadlock. No provedor InMemory (testes) faz read-modify-write via EF, que
/// valida a intenção do comportamento, não o SQL real (limitação documentada
/// no README → Testes).
/// </summary>
public sealed class DailyBalanceRepository(ConsolidationDbContext db)
{
    public async Task ApplyAsync(Guid merchantId, DateOnly date, decimal amount, CancellationToken ct)
    {
        var rounded = Math.Round(amount, 2, MidpointRounding.ToEven);
        var credit = rounded > 0 ? rounded : 0m;
        var debit = rounded < 0 ? -rounded : 0m;
        var now = DateTimeOffset.UtcNow;

        if (db.Database.IsRelational())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO daily_balances (merchant_id, date, total_credits, total_debits, balance, entry_count, updated_at)
                VALUES ({merchantId}, {date}, {credit}, {debit}, {rounded}, 1, {now})
                ON CONFLICT (merchant_id, date) DO UPDATE SET
                    total_credits = daily_balances.total_credits + EXCLUDED.total_credits,
                    total_debits  = daily_balances.total_debits  + EXCLUDED.total_debits,
                    balance       = ROUND(daily_balances.balance + EXCLUDED.balance, 2),
                    entry_count   = daily_balances.entry_count   + 1,
                    updated_at    = EXCLUDED.updated_at
                """, ct);
            return;
        }

        var existing = await db.DailyBalances.FindAsync(new object?[] { merchantId, date }, ct);
        if (existing is null)
            db.DailyBalances.Add(new DailyBalance(merchantId, date, rounded, now));
        else
            existing.Apply(rounded, now);
    }
}
