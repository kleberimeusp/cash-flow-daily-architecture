using CashFlow.Shared.Events;
using MassTransit;

namespace CashFlow.Consolidation.Api.Infrastructure;

/// <summary>
/// Filtro mais externo do pipeline de consumo: quando o retry exponencial do
/// MassTransit se esgota, a exceção sobe até aqui e o evento é movido para a
/// DLQ <c>cashflow.entry.registered.dlq</c> (RNF-03: nada é perdido — o evento
/// fica disponível para intervenção e reprocessamento).
/// </summary>
public sealed class EntryRegisteredDlqFilter(
    ILogger<EntryRegisteredDlqFilter> logger,
    ConsolidationMetrics metrics) : IFilter<ConsumeContext<EntryRegisteredEvent>>
{
    public const string DlqQueueName = "cashflow.entry.registered.dlq";

    public void Probe(ProbeContext context) => context.CreateFilterScope("dlq-forward");

    public async Task Send(ConsumeContext<EntryRegisteredEvent> context, IPipe<ConsumeContext<EntryRegisteredEvent>> next)
    {
        try
        {
            await next.Send(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Evento {EventId} esgotou os retries; movendo para {DlqQueue}",
                context.Message.EventId, DlqQueueName);

            var dlq = await context.GetSendEndpoint(new Uri($"queue:{DlqQueueName}"));
            await dlq.Send(context.Message, send =>
            {
                send.CorrelationId = context.CorrelationId;
                send.Headers.Set("x-source-queue", "cashflow.entry.registered");
                send.Headers.Set("x-exception", ex.GetBaseException().Message);
            }, context.CancellationToken);

            metrics.DlqForwarded();
        }
    }
}
