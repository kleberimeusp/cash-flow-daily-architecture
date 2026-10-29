namespace CashFlow.Consolidation.Api.Application;

/// <summary>
/// Tenant da requisição. RN-06: o comerciante vem sempre do token
/// (claim <c>merchant_id</c>), nunca do corpo da requisição.
/// </summary>
public interface IMerchantContext
{
    Guid MerchantId { get; }
}
