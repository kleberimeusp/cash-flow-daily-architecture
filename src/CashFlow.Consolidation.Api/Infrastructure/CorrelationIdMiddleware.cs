using System.Diagnostics;

namespace CashFlow.Consolidation.Api.Infrastructure;

/// <summary>
/// Garante um correlationId por requisição: vem do header X-Correlation-Id ou é
/// gerado. O valor segue no response e nos logs.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CashFlow.CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        await next(context);
    }
}

public static class CorrelationIdHttpContextExtensions
{
    public static string GetCorrelationId(this HttpContext context)
        => context.Items[CorrelationIdMiddleware.ItemKey] as string
           ?? Guid.NewGuid().ToString("N");
}
