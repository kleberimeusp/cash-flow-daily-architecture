using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace CashFlow.Consolidation.Api.Infrastructure;

/// <summary>
/// Publica as gauges <c>cashflow_consumer_lag</c> (pendentes na fila principal) e
/// <c>cashflow_dlq_depth</c> (mensagens na DLQ) lendo a profundidade real das
/// filas via declare passivo a cada 15s.
/// </summary>
public sealed class QueueDepthService(IConfiguration configuration, ConsolidationMetrics metrics, ILogger<QueueDepthService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMq:Host"] ?? "localhost",
            UserName = configuration["RabbitMq:Username"] ?? "guest",
            Password = configuration["RabbitMq:Password"] ?? "guest",
            AutomaticRecoveryEnabled = true
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = factory.CreateConnection();
                EnsureDlqTopology(connection);
                while (!stoppingToken.IsCancellationRequested && connection.IsOpen)
                {
                    Collect(connection);
                    await Task.Delay(Interval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao coletar profundidade das filas; tentando novamente");
                await Task.Delay(Interval, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Declara a DLQ desde o boot (exchange fanout + fila + binding, mesma
    /// topologia que o MassTransit cria no primeiro envio) para que alertas de
    /// <c>cashflow_dlq_depth</c> funcionem antes da primeira falha.
    /// </summary>
    private void EnsureDlqTopology(IConnection connection)
    {
        try
        {
            using var channel = connection.CreateModel();
            channel.ExchangeDeclare(EntryRegisteredDlqFilter.DlqQueueName, ExchangeType.Fanout, durable: true);
            channel.QueueDeclare(EntryRegisteredDlqFilter.DlqQueueName, durable: true, exclusive: false, autoDelete: false);
            channel.QueueBind(EntryRegisteredDlqFilter.DlqQueueName, EntryRegisteredDlqFilter.DlqQueueName, string.Empty);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao declarar a DLQ; ela será criada no primeiro envio");
        }
    }

    private void Collect(IConnection connection)
    {
        var channel = connection.CreateModel();
        try
        {
            // Declare passivo em fila inexistente derruba o canal (404) — recria antes da segunda leitura.
            var main = TryQueueDepth(channel, "cashflow.entry.registered", out var channelBroken);
            if (channelBroken)
            {
                channel.Dispose();
                channel = connection.CreateModel();
            }

            var dlq = TryQueueDepth(channel, EntryRegisteredDlqFilter.DlqQueueName, out _);

            metrics.SetQueueDepth(main);
            metrics.SetDlqDepth(dlq);
        }
        finally
        {
            channel.Dispose();
        }
    }

    private long TryQueueDepth(IModel channel, string queue, out bool channelBroken)
    {
        channelBroken = false;
        try
        {
            return channel.QueueDeclarePassive(queue).MessageCount;
        }
        catch (OperationInterruptedException)
        {
            channelBroken = true; // fila ainda não declarada — profundidade zero
            return 0;
        }
    }
}
