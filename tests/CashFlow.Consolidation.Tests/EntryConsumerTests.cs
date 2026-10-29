using CashFlow.Consolidation.Api.Application;
using CashFlow.Consolidation.Api.Infrastructure;
using CashFlow.Shared.Events;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CashFlow.Consolidation.Tests;

/// <summary>
/// Usa o provedor InMemory, que não executa o UPSERT nativo (ON CONFLICT) —
/// o repositório cai no caminho read-modify-write. Os testes validam a intenção
/// do comportamento, não o SQL real (limitação documentada no README → Testes;
/// migração para Testcontainers está em Evoluções Futuras).
/// </summary>
public class EntryConsumerTests
{
    private static readonly DateOnly Date = new(2026, 10, 29);

    private static ConsolidationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ConsolidationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // InMemory não suporta transações; o consumer as usa apenas no caminho relacional.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static EntryConsumer NewConsumer(ConsolidationDbContext db)
        => new(db,
            new DailyBalanceRepository(db),
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            new ConsolidationMetrics(),
            NullLogger<EntryConsumer>.Instance);

    private static EntryRegisteredEvent NewEvent(Guid merchantId, decimal amount, Guid? eventId = null)
        => new(eventId ?? Guid.NewGuid(), Guid.NewGuid(), merchantId, amount, Date,
            "Venda", "test-correlation", DateTimeOffset.UtcNow);

    [Fact]
    public async Task CreditoMaisCredito_AcumulaSaldo()
    {
        await using var db = NewDb();
        var consumer = NewConsumer(db);
        var merchant = Guid.NewGuid();

        await consumer.Consume(NewEvent(merchant, 100.00m));
        await consumer.Consume(NewEvent(merchant, 50.00m));

        var balance = await db.DailyBalances.SingleAsync(b => b.MerchantId == merchant && b.Date == Date);
        balance.Balance.Should().Be(150.00m);
        balance.TotalCredits.Should().Be(150.00m);
        balance.TotalDebits.Should().Be(0m);
        balance.EntryCount.Should().Be(2);
    }

    [Fact]
    public async Task Debito_ReduSaldo()
    {
        await using var db = NewDb();
        var consumer = NewConsumer(db);
        var merchant = Guid.NewGuid();

        await consumer.Consume(NewEvent(merchant, 500.00m));
        await consumer.Consume(NewEvent(merchant, -120.00m));

        var balance = await db.DailyBalances.SingleAsync(b => b.MerchantId == merchant && b.Date == Date);
        balance.Balance.Should().Be(380.00m);
        balance.TotalCredits.Should().Be(500.00m);
        balance.TotalDebits.Should().Be(120.00m);
        balance.EntryCount.Should().Be(2);
    }

    [Fact]
    public async Task EventoDuplicado_NaoConsolidaDuasVezes()
    {
        await using var db = NewDb();
        var consumer = NewConsumer(db);
        var merchant = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await consumer.Consume(NewEvent(merchant, 100.00m, eventId));
        await consumer.Consume(NewEvent(merchant, 100.00m, eventId)); // reentrega do broker

        var balance = await db.DailyBalances.SingleAsync(b => b.MerchantId == merchant && b.Date == Date);
        balance.Balance.Should().Be(100.00m);
        balance.EntryCount.Should().Be(1);
        db.ProcessedEvents.Count().Should().Be(1);
    }

    [Fact]
    public async Task MerchantsDiferentes_NaoSeMisturam()
    {
        await using var db = NewDb();
        var consumer = NewConsumer(db);
        var merchantA = Guid.NewGuid();
        var merchantB = Guid.NewGuid();

        await consumer.Consume(NewEvent(merchantA, 200.00m));
        await consumer.Consume(NewEvent(merchantB, -50.00m));

        var balanceA = await db.DailyBalances.SingleAsync(b => b.MerchantId == merchantA);
        var balanceB = await db.DailyBalances.SingleAsync(b => b.MerchantId == merchantB);
        balanceA.Balance.Should().Be(200.00m);
        balanceB.Balance.Should().Be(-50.00m);
    }
}
