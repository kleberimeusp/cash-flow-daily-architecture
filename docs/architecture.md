# Arquitetura — Sistema de Fluxo de Caixa Diário

> Decisão macro registrada em [ADR-000](../ADR-000.md). Este documento detalha
> componentes, fluxos e o dimensionamento.

## Visão de componentes

```
┌──────────────────────────────────────────────────────────────────┐
│                        CLIENTE (Comerciante)                     │
└───────────────┬──────────────────────────────┬───────────────────┘
                │ POST /entries                │ GET /balances
                ▼                              ▼
   ┌────────────────────────┐      ┌────────────────────────────┐
   │  LEDGER API            │      │  CONSOLIDATION API         │
   │  Domain (Entry)        │      │  Query (cache-first)       │
   │  Application           │      │  Consumer (idempotente)    │
   │  Infrastructure        │      │  Infrastructure            │
   └───────────┬────────────┘      └─────────────┬──────────────┘
               │                                 │
     ┌─────────┴──────────┐            ┌─────────┴──────────┐
     │ PostgreSQL         │            │ PostgreSQL         │
     │  entries           │            │  daily_balances    │
     │  outbox_messages   │            │  processed_events  │
     └─────────┬──────────┘            └─────────▲──────────┘
               │                                 │
     Outbox Relay (BackgroundService)            │
               │                                 │
               ▼                                 │
     ┌───────────────────────────────────────────┴──────┐
     │            RabbitMQ                              │
     │   queue: cashflow.entry.registered               │
     │   DLQ  : cashflow.entry.registered.dlq           │
     └──────────────────────────────────────────────────┘
               │
               ▼
     ┌──────────────────────────┐
     │  Prometheus + Grafana    │
     │  OpenTelemetry           │
     └──────────────────────────┘
```

| Serviço | Responsabilidade | Fonte da verdade |
|---|---|---|
| **Ledger API** (`:5081`) | Controle de lançamentos | ✅ Sim |
| **Consolidation API** (`:5082`) | Saldo diário consolidado | ❌ Projeção derivada |

## Fluxo de escrita (lançamento)

```
1. Cliente → POST /api/v1/entries  (Idempotency-Key: <uuid>)
2. Ledger valida → cria Entry
3. INSERT entries + INSERT outbox_messages  ── MESMA TRANSAÇÃO
4. Commit atômico → 201 Created
5. Outbox Relay (poll 500ms) lê pendentes e publica no RabbitMQ
6. Relay marca published_at
```

O passo 3 é a decisão central. Se o evento fosse publicado direto no broker sem
o outbox, existiria uma janela em que o commit falha e o evento já foi publicado
(consolidado à frente do ledger) ou em que o publish falha e o commit passou
(lançamento perdido silenciosamente). O outbox elimina essa janela.

## Fluxo de leitura (consolidado)

```
1. Cliente → GET /api/v1/balances/{date}
2. Consolidation → Redis (cache hit? retorna)
3. Miss → PostgreSQL daily_balances
4. Popula cache (TTL 30s) → retorna
```

## Fluxo de falha — consolidado indisponível

```
Consolidation fora do ar
        │
        ├─ Ledger: 100% operante (nunca chama o consolidado)
        ├─ Eventos acumulam no RabbitMQ (durável, persistido em disco)
        └─ Ao voltar: consumer drena o backlog
                      ├─ dentro do retry → processa normalmente
                      └─ excede retries → DLQ (nada é perdido)
```

Este fluxo responde literalmente ao requisito obrigatório de disponibilidade
(RNF-01). A reconciliação (evolução de curto prazo) lerá de `entries`, não do
broker — funciona mesmo com fila perdida.

## Dimensionamento para 50 req/s

A conta que importa: 50 req/s × 5% = **2,5 req/s de tolerância**.

- Throughput necessário: 50 msg/s × ~1 KB ≈ **50 KB/s** — trivial para RabbitMQ.
- O gargalo real **não** é volume, é **contenção**:
  1. **Prefetch 32 + 4 consumidores concorrentes** — ~8,3 msg/s por consumidor.
  2. **UPSERT atômico** (`ON CONFLICT DO UPDATE`) em vez de read-modify-write.
     Sem isso, requisições concorrentes no mesmo dia geram deadlock e os 5%
     viram 30%.

## Estrutura do código

```
src/
├── CashFlow.Shared/                  # contratos e eventos compartilhados
│   ├── Events/EntryRegisteredEvent.cs
│   └── Results/Result.cs
│
├── CashFlow.Ledger.Api/
│   ├── Domain/Entry.cs               # agregado raiz (append-only)
│   ├── Application/EntryService.cs   # casos de uso + idempotência camada 1
│   ├── Infrastructure/
│   │   ├── LedgerDbContext.cs
│   │   ├── OutboxMessage.cs
│   │   ├── OutboxRelay.cs            # BackgroundService, poll 500ms
│   │   └── ...
│   ├── Migrations/                   # EF Core migrations
│   └── Program.cs                    # Minimal API endpoints
│
└── CashFlow.Consolidation.Api/
    ├── Domain/DailyBalance.cs        # projeção derivada, reconstruível
    ├── Domain/ProcessedEvent.cs      # idempotência camada 2
    ├── Application/EntryConsumer.cs  # consumidor idempotente
    ├── Application/BalanceQueryService.cs  # cache-first
    ├── Infrastructure/...
    ├── Migrations/
    └── Program.cs
```
