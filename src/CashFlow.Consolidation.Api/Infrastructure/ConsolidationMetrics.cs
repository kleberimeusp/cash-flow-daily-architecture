using System.Diagnostics.Metrics;

namespace CashFlow.Consolidation.Api.Infrastructure;

/// <summary>
/// Métricas de negócio do Consolidation (ver docs/observability.md). Nomes
/// seguem a convenção do exportador Prometheus (counter → _total, unidade "s" →
/// _seconds).
/// </summary>
public sealed class ConsolidationMetrics
{
    private readonly Counter<long> _balanceConsolidated;
    private readonly Counter<long> _dlqForwarded;
    private readonly Histogram<double> _consolidationLag;
    private long _queueDepth;
    private long _dlqDepth;

    public ConsolidationMetrics()
    {
        var meter = new Meter("CashFlow.Consolidation");
        _balanceConsolidated = meter.CreateCounter<long>(
            "cashflow_balance_consolidated", description: "Eventos consolidados por comerciante");
        _dlqForwarded = meter.CreateCounter<long>(
            "cashflow_dlq_forwarded", description: "Eventos movidos para a DLQ após esgotar retries");
        _consolidationLag = meter.CreateHistogram<double>(
            "cashflow_consolidation_lag", unit: "s",
            description: "Atraso entre o registro no Ledger e a consolidação");
        meter.CreateObservableGauge("cashflow_consumer_lag", () => _queueDepth,
            description: "Mensagens pendentes na fila cashflow.entry.registered");
        meter.CreateObservableGauge("cashflow_dlq_depth", () => _dlqDepth,
            description: "Mensagens na DLQ — qualquer valor > 0 exige intervenção");
    }

    public void BalanceConsolidated(Guid merchantId)
        => _balanceConsolidated.Add(1, new KeyValuePair<string, object?>("merchant", merchantId.ToString()));

    public void DlqForwarded() => _dlqForwarded.Add(1);

    public void ConsolidationLag(double seconds) => _consolidationLag.Record(seconds);

    public void SetQueueDepth(long value) => _queueDepth = value;

    public void SetDlqDepth(long value) => _dlqDepth = value;
}
