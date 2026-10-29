using System.Diagnostics.Metrics;

namespace CashFlow.Ledger.Api.Infrastructure;

/// <summary>
/// Métricas de negócio do Ledger (ver docs/observability.md). Os nomes seguem a
/// convenção do exportador Prometheus: counters ganham sufixo _total e o
/// histograma com unidade "s" ganha sufixo _seconds.
/// </summary>
public sealed class LedgerMetrics
{
    private readonly Counter<long> _entriesRegistered;
    private readonly Histogram<double> _outboxPublishDuration;
    private int _pendingOutbox;

    public LedgerMetrics()
    {
        var meter = new Meter("CashFlow.Ledger");
        _entriesRegistered = meter.CreateCounter<long>(
            "cashflow_entries_registered", description: "Lançamentos registrados por comerciante e tipo");
        _outboxPublishDuration = meter.CreateHistogram<double>(
            "cashflow_outbox_publish_duration", unit: "s", description: "Duração da publicação do outbox no broker");
        meter.CreateObservableGauge(
            "cashflow_outbox_pending_count", () => _pendingOutbox,
            description: "Mensagens pendentes no outbox (sinal precoce de broker lento/fora)");
    }

    public void EntryRegistered(Guid merchantId, string type)
        => _entriesRegistered.Add(1,
            new KeyValuePair<string, object?>("merchant", merchantId.ToString()),
            new KeyValuePair<string, object?>("type", type));

    public void OutboxPublished(double seconds) => _outboxPublishDuration.Record(seconds);

    public void SetPendingOutbox(int value) => _pendingOutbox = value;
}
