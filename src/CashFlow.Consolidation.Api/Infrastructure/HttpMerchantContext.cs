using CashFlow.Consolidation.Api.Application;

namespace CashFlow.Consolidation.Api.Infrastructure;

/// <summary>
/// Resolve o tenant da requisição. RN-06: o comerciante vem do token
/// (claim <c>merchant_id</c>), nunca do corpo da requisição.
/// JWT não está configurado nesta entrega (ver README → Evoluções Futuras):
/// em desenvolvimento, usa-se o header X-Merchant-Id ou o comerciante de teste.
/// </summary>
public sealed class HttpMerchantContext(IHttpContextAccessor accessor) : IMerchantContext
{
    /// <summary>Comerciante de teste usado em ambiente de desenvolvimento.</summary>
    public static readonly Guid DevMerchantId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    public Guid MerchantId => Resolve();

    private Guid Resolve()
    {
        var http = accessor.HttpContext;

        var claim = http?.User.FindFirst("merchant_id")?.Value;
        if (Guid.TryParse(claim, out var fromClaim))
            return fromClaim;

        var header = http?.Request.Headers["X-Merchant-Id"].FirstOrDefault();
        if (Guid.TryParse(header, out var fromHeader))
            return fromHeader;

        return DevMerchantId;
    }
}
