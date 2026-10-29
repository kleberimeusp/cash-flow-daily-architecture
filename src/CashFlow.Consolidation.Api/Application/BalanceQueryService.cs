using System.Text.Json;
using CashFlow.Consolidation.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace CashFlow.Consolidation.Api.Application;

/// <summary>
/// Consulta cache-first (ADR-006): Redis com TTL de 30s na frente do Postgres.
/// Falha de Redis degrada para leitura direta — cache nunca derruba a consulta.
/// </summary>
public sealed class BalanceQueryService(
    ConsolidationDbContext db,
    IDistributedCache cache,
    IMerchantContext merchantContext,
    ILogger<BalanceQueryService> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public static string CacheKey(Guid merchantId, DateOnly date)
        => $"balance:{merchantId}:{date:yyyy-MM-dd}";

    public async Task<BalanceResponse> GetDailyAsync(DateOnly date, CancellationToken ct)
    {
        var merchantId = merchantContext.MerchantId;
        var key = CacheKey(merchantId, date);

        var cached = await TryGetAsync(key, ct);
        if (cached is not null)
            return cached;

        var entity = await db.DailyBalances.AsNoTracking()
            .FirstOrDefaultAsync(b => b.MerchantId == merchantId && b.Date == date, ct);

        var response = entity is null
            ? BalanceResponse.Empty(merchantId, date)
            : BalanceResponse.From(entity);

        await TrySetAsync(key, response, ct);
        return response;
    }

    public async Task<IReadOnlyList<BalanceResponse>> GetRangeAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var merchantId = merchantContext.MerchantId;
        return await db.DailyBalances.AsNoTracking()
            .Where(b => b.MerchantId == merchantId && b.Date >= from && b.Date <= to)
            .OrderBy(b => b.Date)
            .Select(b => BalanceResponse.From(b))
            .ToListAsync(ct);
    }

    private async Task<BalanceResponse?> TryGetAsync(string key, CancellationToken ct)
    {
        try
        {
            var json = await cache.GetStringAsync(key, ct);
            return json is null ? null : JsonSerializer.Deserialize<BalanceResponse>(json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao ler cache; consultando o banco diretamente");
            return null;
        }
    }

    private async Task TrySetAsync(string key, BalanceResponse response, CancellationToken ct)
    {
        try
        {
            await cache.SetStringAsync(key, JsonSerializer.Serialize(response),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao gravar cache; seguindo sem cache");
        }
    }
}
