using System.Diagnostics;
using System.Text.Json;
using CashFlow.Shared.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Ledger.Api.Infrastructure;

/// <summary>
/// Relay do Transactional Outbox (ADR-001): faz poll a cada 500 ms nos registros
/// pendentes e publica no RabbitMQ. Se o broker estiver fora, os eventos ficam
/// retidos na tabela (RPO ≈ 0) e são publicados quando ele volta — o Ledger
/// continua operante, que é exatamente o RNF-01.
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    LedgerMetrics metrics,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Broker fora / banco indisponível: tenta de novo no próximo ciclo.
                logger.LogError(ex, "Falha na iteração do outbox relay");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    internal async Task<int> PublishPendingAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        // IPublishEndpoint é scoped no MassTransit — resolve do escopo da iteração.
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        metrics.SetPendingOutbox(await db.OutboxMessages.CountAsync(o => o.PublishedAt == null, ct));

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // SKIP LOCKED: múltiplas instâncias do relay não se bloqueiam nem duplicam leitura.
        var pending = await db.OutboxMessages
            .FromSqlRaw($"""
                SELECT id, type, payload, correlation_id, created_at, published_at
                FROM outbox_messages
                WHERE published_at IS NULL
                ORDER BY created_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        foreach (var message in pending)
        {
            var evt = JsonSerializer.Deserialize<EntryRegisteredEvent>(message.Payload)
                ?? throw new InvalidOperationException($"Payload inválido no outbox {message.Id}");

            var started = Stopwatch.GetTimestamp();
            await publishEndpoint.Publish(evt, send =>
                send.Headers.Set(CorrelationIdMiddleware.HeaderName, message.CorrelationId), ct);
            metrics.OutboxPublished(Stopwatch.GetElapsedTime(started).TotalSeconds);

            message.PublishedAt = DateTimeOffset.UtcNow;
        }

        // Se o commit falhar após o publish, o evento é republicado no próximo
        // ciclo — a duplicidade é absorvida pelo consumidor (ADR-005, camada 2).
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (pending.Count > 0)
            logger.LogInformation("Outbox relay publicou {Count} evento(s)", pending.Count);

        return pending.Count;
    }
}
