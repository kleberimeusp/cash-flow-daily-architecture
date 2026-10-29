README.md — Cash Flow Daily

# 💰 Cash Flow Daily — Arquitetura de Referência

Sistema de controle de fluxo de caixa diário para comerciantes, com registro de
lançamentos (débitos e créditos) e consolidação diária de saldo.

Este repositório é a entrega do **Desafio de Arquitetura de Soluções** e contém
não apenas o código funcional, mas também o raciocínio arquitetural completo:
mapeamento de domínios, capacidades de negócio, decisões registradas em ADRs,
estimativa de custos, arquitetura de transição e estratégia de observabilidade.

---

## 📋 Índice

- [Visão Geral](#-visão-geral)
- [Decisão de Arquitetura em 30 Segundos](#-decisão-de-arquitetura-em-30-segundos)
- [Requisitos](#-requisitos)
- [Domínios e Capacidades](#-domínios-e-capacidades)
- [Arquitetura](#-arquitetura)
- [Stack Tecnológica](#-stack-tecnológica)
- [Como Rodar Localmente](#-como-rodar-localmente)
- [API Reference](#-api-reference)
- [Testes](#-testes)
- [Observabilidade](#-observabilidade)
- [Estrutura do Repositório](#-estrutura-do-repositório)
- [Decisões Arquiteturais (ADRs)](#-decisões-arquiteturais-adrs)
- [Estimativa de Custos](#-estimativa-de-custos)
- [Arquitetura de Transição](#-arquitetura-de-transição)
- [Evoluções Futuras](#-evoluções-futuras)

---

## 🎯 Visão Geral

O sistema é composto por **dois serviços independentes**:

| Serviço | Responsabilidade | Porta | Fonte da verdade |
|---|---|---|---|
| **Ledger API** | Controle de lançamentos (débitos/créditos) | `5081` | ✅ Sim |
| **Consolidation API** | Saldo diário consolidado | `5082` | ❌ Projeção derivada |

A separação não é cosmética. Ela é a **resposta direta ao requisito não
funcional mais crítico**:

> *"O serviço de controle de lançamento não deve ficar indisponível se o
> sistema de consolidado diário cair."*

O Ledger nunca chama o Consolidation de forma síncrona obrigatória. O
Consolidation é uma **projeção reconstruível** a partir do Ledger — se ele cair,
nenhum lançamento é perdido e o Ledger continua operando a 100%.

---

## ⚡ Decisão de Arquitetura em 30 Segundos

```
Cliente → POST /entries → Ledger API
                              │
                              ├─ INSERT entries          ┐
                              └─ INSERT outbox_messages  ┘ MESMA TRANSAÇÃO
                                        │
                              Outbox Relay (BackgroundService)
                                        │
                                        ▼
                                   RabbitMQ
                                        │
                                        ▼
                              Consolidation API
                                        │
                              UPSERT daily_balances
                                   (idempotente)
```

**Por que Outbox e não publicar direto no broker?**

Se o serviço publicar no RabbitMQ e o commit no banco falhar (ou o inverso), os
dois sistemas divergem silenciosamente. O padrão Outbox grava o evento **na
mesma transação** do agregado. Um relay lê os pendentes e publica. Broker fora =
evento espera no outbox, não se perde. Isso atende simultaneamente:

- **RNF-01** (Lançamentos indisponível se consolidado cair) — desacoplamento temporal
- **RNF-03** (≤ 5% de perda no pico) — fila com retry, não HTTP bloqueante
- **RNF-08** (RPO ≈ 0) — nada é perdido em falha de broker

---

## 📐 Requisitos

### Requisitos Funcionais

| ID | Requisito | Serviço |
|---|---|---|
| RF-01 | Registrar lançamento (débito/crédito) com valor, data de competência, descrição e correlação | Ledger |
| RF-02 | Consultar lançamento por id | Ledger |
| RF-03 | Listar lançamentos por período | Ledger |
| RF-04 | Estornar lançamento (append-only — sem delete) | Ledger |
| RF-05 | Publicar evento `EntryRegistered` | Ledger |
| RF-06 | Manter saldo diário por comerciante | Consolidation |
| RF-07 | Consultar consolidado de uma data | Consolidation |
| RF-08 | Consultar consolidado por intervalo | Consolidation |
| RF-09 | Reprocessar período (reconciliação) | Consolidation |
| RF-10 | Idempotência na recepção de eventos | Consolidation |

### Requisitos Não Funcionais

| ID | Requisito | Meta | Como é atendido |
|---|---|---|---|
| RNF-01 | Lançamentos disponível com Consolidado fora | 99,95% | Broker desacoplado + outbox |
| RNF-02 | Consolidado suporta pico | **50 req/s** | Prefetch 32 + 4 consumidores |
| RNF-03 | Perda máxima no pico | **≤ 5%** | Retry exponencial + DLQ + backpressure |
| RNF-04 | Latência consulta consolidado | p95 < 150 ms | Redis cache + índice composto |
| RNF-05 | Latência escrita lançamento | p95 < 200 ms | Escrita local + outbox no commit |
| RNF-06 | Segurança no consumo | — | JWT RS256 + claim `merchant_id` + mTLS |
| RNF-07 | Observabilidade | — | OpenTelemetry + Prometheus + Grafana |
| RNF-08 | Recuperação de falha | RPO ≈ 0, RTO < 5 min | Outbox + replicação Postgres |
| RNF-09 | Auditabilidade | 100% | Event log imutável e versionado |

### Regras de Negócio Explícitas

**RN-01 — Append-only com estorno.**
Lançamentos nunca são alterados nem removidos. Um estorno é um novo lançamento
de sinal oposto que referencia o original via `reversesEntryId`. Garante trilha
de auditoria e torna a reconciliação um recálculo determinístico.

**RN-02 — Consolida pela data de competência.**
A data que entra no consolidado é `occurredOn`, não a data de processamento.
Sem isso, um pico às 23:59 jogaria lançamentos para o dia seguinte. Desacoplar
"quando aconteceu" de "quando foi processado" é o que permite replay e
tolerância a atraso.

**RN-03 — Débito é valor negativo.**
Não existe enum `Debit`/`Credit`. Um débito é `Amount < 0`. Isso elimina a
possibilidade de somar um débito como crédito — a regra `saldo = SUM(amount)`
não pode ser implementada errado.

**RN-04 — Arredondamento bancário.**
Valores são arredondados com `MidpointRounding.ToEven` em 2 casas. O
arredondamento comercial (`AwayFromZero`) acumula viés sistemático que aparece
como divergência de centavos na reconciliação diária.

---

## 🗺️ Domínios e Capacidades

### Capacidades de Negócio

```
Gestão de Fluxo de Caixa (L1)
├── Registrar Movimentação Financeira          [core]
│   ├── Classificar lançamento (débito/crédito)
│   ├── Estornar movimentação
│   └── Consultar histórico de lançamentos
├── Consolidar Posição Financeira Diária        [core]
│   ├── Calcular saldo diário
│   ├── Recalcular período (reconciliação)
│   └── Publicar saldo consolidado
├── Garantir Integridade Contábil               [supporting]
│   ├── Detectar duplicidade
│   └── Reconciliar divergências
└── Governar Acesso e Auditoria                 [generic]
    ├── Autenticar e autorizar
    └── Rastrear operações
```

### Bounded Contexts (DDD)

| Contexto | Tipo | Agregado raiz | Responsabilidade |
|---|---|---|---|
| **Ledger** | Core | `Entry` | Verdade absoluta das movimentações |
| **Consolidation** | Core | `DailyBalance` | Projeção derivada, reconstruível |
| **Identity** | Generic | `Merchant` | Tenancy e autorização |

O Consolidation **nunca** é fonte da verdade. Ele pode ser integralmente
reconstruído a partir do Ledger — é isso que justifica o endpoint de
reconciliação (RF-09) e permite o desacoplamento total de disponibilidade.

### Linguagem Ubíqua

| Termo | Definição |
|---|---|
| `Entry` | Lançamento imutável. Positivo = crédito, negativo = débito |
| `Reversal` | Entry que anula outra via `reversesEntryId` |
| `DailyBalance` | Soma líquida das entries de um comerciante em um dia |
| `CorrelationId` | Rastreia a requisição por todo o sistema |
| `IdempotencyKey` | Impede duplicidade de um mesmo lançamento |

---

## 🏗️ Arquitetura

### Estilo escolhido: Microsserviços + Outbox Pattern + Broker

| Alternativa | Por que não foi escolhida |
|---|---|
| **Monolito modular** | Não demonstra o isolamento exigido pelo RNF-01. O requisito é explicitamente "um não pode derrubar o outro". |
| **Serverless** | Consolidado a 50 req/s com estado por dia e cache quente — cold start e limite de conexão a banco jogam contra. Custo imprevisível. |
| **Microsserviços REST síncrono puro** | Acoplamento temporal. Se o consolidado cai, o lançamento cai. Viola RNF-01 diretamente. |
| **Microsserviços + Outbox + Broker** ✅ | Desacoplamento temporal real, isolamento de falha, replay nativo, sem perda de eventos. |

### Diagrama de Componentes

```
┌──────────────────────────────────────────────────────────────────┐
│                        CLIENTE (Comerciante)                     │
└───────────────┬──────────────────────────────┬───────────────────┘
                │ POST /entries                │ GET /balances
                ▼                              ▼
   ┌────────────────────────┐      ┌────────────────────────────┐
   │  LEDGER API            │      │  CONSOLIDATION API         │
   │  (Minimal API)         │      │  (Minimal API)             │
   │  ┌──────────────────┐  │      │  ┌──────────────────────┐  │
   │  │ Application      │  │      │  │ Query (cache-first)  │  │
   │  │ Domain (Entry)   │  │      │  │ Domain (Balance)     │  │
   │  │ Infrastructure   │  │      │  │ Infrastructure       │  │
   │  └────────┬─────────┘  │      │  └──────────┬───────────┘  │
   └───────────┼────────────┘      └─────────────┼──────────────┘
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

### Fluxo — Registro de Lançamento

```
1. Cliente → POST /api/v1/entries  (Idempotency-Key: <uuid>)
2. Ledger: valida → cria Entry → INSERT entries + INSERT outbox_messages
              (MESMA TRANSAÇÃO) ─────────────────────┐
3. Ledger → 201 Created + Location                     │ commit atômico
4. Outbox Relay (poll 500ms) → lê pendentes            │
5. Relay → publish no RabbitMQ → marca published  ◄────┘
6. Consolidation Consumer → recebe evento
7. Consumer: verifica processed_events (idempotência por eventId)
              ├─ já processado → ACK e descarta
              └─ novo → UPSERT daily_balances (+amount, occurredOn)
                          INSERT processed_events (mesma transação)
8. ACK
```

### Fluxo de Falha — Consolidado Fora do Ar

```
Consolidado fora do ar
        │
        ├─ Lançamentos: 100% operante (nunca chama o consolidado)
        ├─ Eventos: acumulam no RabbitMQ (durável, persistido em disco)
        └─ Ao voltar: consumer drena o backlog
                      ├─ dentro do TTL → processa normalmente
                      └─ excede retries → DLQ (nada é perdido)
                                                │
                              operador roda POST /api/v1/balances/reconcile
                              que recalcula do Ledger (fonte da verdade)
```

**Isso responde literalmente ao requisito obrigatório.** E a reconciliação não
depende do broker — lê do `entries`, então funciona mesmo com fila perdida.

### Dimensionamento para 50 req/s

A conta que importa: 50 req/s × 5% = **2,5 req/s de tolerância**.

- Throughput necessário: 50 msg/s × ~1 KB ≈ **50 KB/s** — trivial para RabbitMQ.
- O gargalo real **não** é volume, é **contenção**. Duas mitigações:
  1. **Prefetch 32 + 4 consumidores concorrentes** — cada um processa ~8,3 msg/s.
  2. **UPSERT atômico** (`ON CONFLICT DO UPDATE`) em vez de read-modify-write.
     Sem isso, 50 req/s concorrentes no mesmo dia geram deadlock e os 5% viram 30%.

---

## 🛠️ Stack Tecnológica

| Camada | Tecnologia | Justificativa |
|---|---|---|
| Runtime | **.NET 8 / C# 12** | Alta performance, Minimal APIs eliminam boilerplate, ecossistema maduro |
| API | **ASP.NET Core Minimal APIs** | Startup rápido, menos cerimônia, ideal para serviços pequenos |
| ORM | **EF Core 8** | Migrations, LINQ type-safe, `ExecuteSqlInterpolatedAsync` para UPSERT |
| Banco | **PostgreSQL 16** | ACID, `ON CONFLICT` nativo, `jsonb` para outbox, custo zero |
| Mensageria | **RabbitMQ 3.13** | Fila dedicada por evento, DLQ nativa, management UI, menor custo operacional que Kafka |
| Cliente de mensageria | **MassTransit** | Retry, DLQ, redelivery, observabilidade integrada |
| Cache | **Redis 7** | Cache read-through do consolidado, TTL curto (30s) |
| Observabilidade | **OpenTelemetry + Prometheus + Grafana** | Padrão de mercado, sem vendor lock-in |
| Testes | **xUnit + FluentAssertions + Moq** | Idempotência e regras de domínio |
| Infra local | **Docker Compose** | Um comando sobe tudo |

### Por que RabbitMQ e não Kafka?

| Critério | RabbitMQ | Kafka |
|---|---|---|
| Volume deste projeto | ~50 msg/s | Overkill (Kafka brilha em 100k+/s) |
| Custo operacional | Baixo | Alto (Zookeeper/Controller, tuning) |
| DLQ | Nativa, trivial | Manual (tópicos adicionais) |
| Ordenação | Por consumer | Por partição (melhor em escala) |
| Custo AWS | ~USD 180/mês (Amazon MQ) | ~USD 120/mês serverless, mas mais setup |

**Decisão:** RabbitMQ para o volume atual. O código usa MassTransit, então a
migração para Kafka é troca de transporte, não reescrita. **Ponto de virada
documentado:** se o volume passar de 5.000 msg/s ou se for necessário replay
histórico em massa, migrar para Kafka com `PartitionKey = merchantId`.

---

## 🚀 Como Rodar Localmente

### Pré-requisitos

- Docker 24+ e Docker Compose v2
- .NET 8 SDK (apenas para rodar/testar sem Docker)
- `curl` ou similar para os exemplos

### Subir tudo com Docker

```bash
# 1. Clone o repositório
git clone https://github.com/<seu-usuario>/cashflow.git
cd cashflow

# 2. Suba toda a infraestrutura + aplicações
docker compose -f deploy/docker-compose.yml up --build

# 3. Acompanhe os logs (opcional)
docker compose -f deploy/docker-compose.yml logs -f ledger-api consolidation-api
```

**Aguarde ~30 segundos** na primeira execução (build das imagens .NET).

### Serviços disponíveis

| Serviço | URL | Credenciais |
|---|---|---|
| **Ledger API** | http://localhost:5081 | — |
| **Ledger Swagger** | http://localhost:5081/swagger | — |
| **Consolidation API** | http://localhost:5082 | — |
| **Consolidation Swagger** | http://localhost:5082/swagger | — |
| **RabbitMQ Management** | http://localhost:15672 | `cashflow` / `cashflow` |
| **Prometheus** | http://localhost:9090 | — |
| **Grafana** | http://localhost:3000 | `admin` / `admin` |
| **PostgreSQL** | `localhost:5433` | `cashflow` / `cashflow` |

### Verificar saúde

```bash
curl http://localhost:5081/health    # Ledger
curl http://localhost:5082/health    # Consolidation
curl http://localhost:5081/metrics | head
```

### Rodar sem Docker (apenas as aplicações)

```bash
# Suba só a infra
docker compose -f deploy/docker-compose.yml up -d postgres rabbitmq redis

# Restore + build
dotnet restore
dotnet build

# Terminal 1 — Ledger
dotnet run --project src/CashFlow.Ledger.Api

# Terminal 2 — Consolidation
dotnet run --project src/CashFlow.Consolidation.Api
```

### Parar e limpar

```bash
# Parar
docker compose -f deploy/docker-compose.yml down

# Parar E apagar volumes (reset de dados)
docker compose -f deploy/docker-compose.yml down -v
```

---

## 📡 API Reference

> **Nota:** autenticação JWT está planejada mas não configurada nesta entrega
> (ver [Evoluções Futuras](#-evoluções-futuras)). Em ambiente de desenvolvimento,
> o header `Authorization` é ignorado e um `merchantId` de teste é usado.

### Ledger API — `http://localhost:5081`

#### `POST /api/v1/entries` — Registrar lançamento

**Headers obrigatórios:**
- `Content-Type: application/json`
- `Idempotency-Key: <uuid>` — chave única da operação

```bash
curl -X POST http://localhost:5081/api/v1/entries \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{
    "amount": 150.00,
    "occurredOn": "2026-10-29",
    "description": "Venda de balcão"
  }'
```

**Request body:**

| Campo | Tipo | Obrigatório | Descrição |
|---|---|---|---|
| `amount` | `decimal` | ✅ | Positivo = crédito, negativo = débito. Não pode ser zero. |
| `occurredOn` | `DateOnly` (`YYYY-MM-DD`) | ✅ | Data de competência. Não pode ser futura. |
| `description` | `string` | ✅ | Máx. 280 caracteres. |

**Response `201 Created`:**
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "merchantId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "amount": 150.00,
  "occurredOn": "2026-10-29",
  "description": "Venda de balcão",
  "reversesEntryId": null,
  "idempotencyKey": "8f14e45f-ceea-467f-a8b3-1c2d3e4f5a6b",
  "correlationId": "0HN7ABC123",
  "createdAt": "2026-10-29T14:32:11.123Z"
}
```

**Response `200 OK` (idempotência):** mesma chave + mesmo comerciante retorna o
lançamento original, sem duplicar.

**Response `400 Bad Request`:**
```json
{
  "errors": {
    "entry": ["O valor do lançamento não pode ser zero."]
  }
}
```

#### `GET /api/v1/entries/{id}` — Consultar lançamento

```bash
curl http://localhost:5081/api/v1/entries/3fa85f64-5717-4562-b3fc-2c963f66afa6
```

`200 OK` com o objeto · `404 Not Found` se não existir **ou pertencer a outro
comerciante** (o filtro por tenant vai na query, impede IDOR).

#### `GET /api/v1/entries?from=&to=` — Listar por período

```bash
curl "http://localhost:5081/api/v1/entries?from=2026-10-01&to=2026-10-29"
```

| Query param | Tipo | Default | Limite |
|---|---|---|---|
| `from` | `DateOnly` | hoje − 30 dias | — |
| `to` | `DateOnly` | hoje | Intervalo máx. 366 dias |

#### `POST /api/v1/entries/{id}/reverse` — Estornar

Cria um novo lançamento de sinal oposto. O original permanece intacto.

```bash
curl -X POST http://localhost:5081/api/v1/entries/3fa85f64-.../reverse
```

`201 Created` com o estorno · `404` se o original não existir ·
`409` se o lançamento já for um estorno.

---

### Consolidation API — `http://localhost:5082`

#### `GET /api/v1/balances/{date}` — Consolidado de um dia

```bash
curl http://localhost:5082/api/v1/balances/2026-10-29
```

**Response `200 OK`:**
```json
{
  "merchantId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "date": "2026-10-29",
  "totalCredits": 450.00,
  "totalDebits": 120.00,
  "balance": 330.00,
  "entryCount": 5,
  "updatedAt": "2026-10-29T14:32:15.456Z"
}
```

Consulta é **cache-first** (Redis, TTL 30s). Dia sem movimentação retorna zeros
com `200`, não `404` — a ausência de lançamento é um fato de negócio válido.

#### `GET /api/v1/balances?from=&to=` — Consolidado por intervalo

```bash
curl "http://localhost:5082/api/v1/balances?from=2026-10-01&to=2026-10-29"
```

Retorna array ordenado por data. Intervalo máximo de 366 dias.

---

### Exemplo ponta a ponta

```bash
# 1. Registra um crédito
KEY1=$(uuidgen)
curl -X POST http://localhost:5081/api/v1/entries \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $KEY1" \
  -d '{"amount": 500.00, "occurredOn": "2026-10-29", "description": "Venda"}'

# 2. Registra um débito
curl -X POST http://localhost:5081/api/v1/entries \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"amount": -120.00, "occurredOn": "2026-10-29", "description": "Compra"}'

# 3. Aguarda ~1s (consolidação é eventual) e consulta
sleep 1
curl http://localhost:5082/api/v1/balances/2026-10-29

# Esperado: balance = 380.00, entryCount = 2
```

---

## 🧪 Testes

```bash
# Todos os testes
dotnet test

# Com cobertura
dotnet test --collect:"XPlat Code Coverage"

# Projeto específico
dotnet test tests/CashFlow.Ledger.Tests
dotnet test tests/CashFlow.Consolidation.Tests
```

### Cobertura de testes desta entrega

| Arquivo | Cenários cobertos |
|---|---|
| `CashFlow.Ledger.Tests/EntryTests` | Criação válida · valor zero rejeitado · `merchantId` vazio rejeitado · data futura rejeitada · arredondamento `ToEven` |
| `CashFlow.Consolidation.Tests/EntryConsumerTests` | Crédito + crédito acumula · débito reduz saldo · evento duplicado não consolida 2× · merchants diferentes não se misturam |

### ⚠️ Limitação conhecida dos testes

Os testes de `EntryConsumer` usam `UseInMemoryDatabase`, que **não executa
`ExecuteSqlInterpolatedAsync`** — o UPSERT nativo (`ON CONFLICT`) não roda em
memória. Eles validam a **intenção** do comportamento, não o SQL real.

**Correção planejada:** migrar para **Testcontainers** com Postgres real no CI.
Isso está registrado em [Evoluções Futuras](#-evoluções-futuras) e é o próximo
passo antes de qualquer entrega a produção.

---

## 📊 Observabilidade

### Métricas expostas (`/metrics`)

**Negócio:**
```
cashflow_entries_registered_total{merchant,type}
cashflow_outbox_pending_count
cashflow_outbox_publish_duration_seconds
cashflow_consumer_lag
cashflow_balance_consolidated_total
cashflow_dlq_depth
```

**Técnicas:** latência HTTP p95/p99 por endpoint, taxa de erro 4xx/5xx, pool de
conexões do Postgres, CPU/memória por pod.

### SLIs e SLOs

| SLI | SLO | Alerta |
|---|---|---|
| Disponibilidade Ledger | 99,95% / 30 dias | Error budget < 20% |
| Latência p95 `POST /entries` | < 200 ms | > 300 ms por 5 min |
| Lag de consolidação | < 5 s p95 | > 30 s por 2 min |
| Perda em pico | ≤ 5% | > 3% |
| Profundidade da DLQ | 0 | **qualquer valor > 0** |

### Alertas que realmente importam

1. **`cashflow_outbox_pending_count > 1000` por 5 min** — broker lento ou fora.
   É o sinal mais precoce de problema de acoplamento.
2. **`cashflow_dlq_depth > 0`** — houve evento que não pôde ser processado.
   Requer intervenção humana; nunca deve ser ignorado.
3. **`cashflow_consumer_lag > 30s`** — consolidação atrasando. Se crescer
   indefinidamente, o RNF-02 está em risco.

### Tracing

`correlationId` propaga do cliente → Ledger → outbox → broker → consumer →
banco. MassTransit injeta contexto automaticamente. Um único trace mostra a
jornada completa do lançamento.

### Dashboard Grafana sugerido

Quatro painéis: volume de lançamentos (série temporal), lag de consolidação
(gauge), outbox pendente (série), erros por tipo (barras).

---

## 📁 Estrutura do Repositório

```
cashflow/
├── README.md                          # este arquivo
├── CashFlow.sln
│
├── docs/
│   ├── architecture.md                # arquitetura detalhada + diagramas
│   ├── domain-model.md                # DDD, agregados, linguagem ubíqua
│   ├── requirements.md                # RF + RNF + regras de negócio
│   ├── transition-architecture.md     # migração de legado (fases 0-3)
│   ├── observability.md               # métricas, SLOs, alertas
│   ├── costs.md                       # estimativa de custos AWS
│   └── adr/
│       ├── 001-microservices-with-outbox.md
│       ├── 002-rabbitmq-over-kafka.md
│       ├── 003-postgresql.md
│       ├── 004-append-only-ledger.md
│       ├── 005-idempotency-strategy.md
│       └── 006-cache-strategy.md
│
├── src/
│   ├── CashFlow.Shared/               # contratos e eventos compartilhados
│   │   ├── Events/EntryRegisteredEvent.cs
│   │   └── Results/Result.cs
│   │
│   ├── CashFlow.Ledger.Api/
│   │   ├── Domain/Entry.cs            # agregado raiz
│   │   ├── Application/EntryService.cs
│   │   ├── Infrastructure/
│   │   │   ├── LedgerDbContext.cs
│   │   │   ├── OutboxMessage.cs
│   │   │   └── OutboxRelay.cs
│   │   ├── Program.cs                 # Minimal API endpoints
│   │   └── Dockerfile
│   │
│   └── CashFlow.Consolidation.Api/
│       ├── Domain/DailyBalance.cs
│       ├── Application/EntryConsumer.cs
│       ├── Infrastructure/ConsolidationDbContext.cs
│       ├── Program.cs
│       └── Dockerfile
│
├── tests/
│   ├── CashFlow.Ledger.Tests/
│   └── CashFlow.Consolidation.Tests/
│
└── deploy/
    ├── docker-compose.yml
    ├── init-db.sql
    └── prometheus/prometheus.yml
```

---

## 📝 Decisões Arquiteturais (ADRs)

### ADR-001 — Microsserviços com Outbox Pattern

**Status:** Aceito

**Contexto:** O sistema tem dois domínios com perfis de carga e requisitos de
disponibilidade distintos. O RNF-01 exige que o Ledger opere mesmo com o
Consolidation indisponível.

**Decisão:** Dois serviços independentes, integrados por broker, com o padrão
Outbox transacional no Ledger para garantir publicação confiável.

**Consequências:**
- ✅ Isolamento de falha real, não apenas nominal
- ✅ Replay/reconciliação nativa
- ✅ Consistência eventual com janela mensurável (~ms)
- ⚠️ Cliente vê o saldo com atraso de consolidação — aceitável para fluxo diário
- ⚠️ Complexidade operacional maior que monolito

---

### ADR-002 — RabbitMQ em vez de Kafka

**Status:** Aceito

**Contexto:** Volume previsto de ~50 msg/s no pico, uma fila lógica, necessidade
de DLQ robusta e baixo custo operacional.

**Decisão:** RabbitMQ 3.13 com MassTransit.

**Consequências:**
- ✅ DLQ nativa e simples, management UI, menor curva operacional
- ✅ Custo AWS via Amazon MQ previsível (~USD 180/mês)
- ⚠️ Não escala para centenas de milhares de msg/s — ponto de virada documentado
  em 5.000 msg/s ou necessidade de replay histórico em massa
- ✅ MassTransit permite trocar transporte sem reescrever a aplicação

---

### ADR-003 — PostgreSQL como banco de ambos os serviços

**Status:** Aceito

**Contexto:** Necessidade de ACID, UPSERT atômico, índice parcial e baixo custo.

**Decisão:** PostgreSQL 16 para os dois serviços (schemas/bancos separados).

**Consequências:**
- ✅ `ON CONFLICT DO UPDATE` atômico — essencial para 50 req/s sem deadlock
- ✅ `jsonb` para payload do outbox + índice parcial `WHERE processed_at IS NULL`
- ✅ Custo zero de licença, Multi-AZ gerenciado no RDS
- ⚠️ Escala vertical limitada — sharding por `merchantId` é a evolução natural

---

### ADR-004 — Ledger append-only com estorno

**Status:** Aceito

**Contexto:** Sistema financeiro exige auditabilidade. Alterar ou apagar
lançamentos destrói a trilha e inviabiliza reconciliação determinística.

**Decisão:** Lançamentos são imutáveis. Estorno é um novo lançamento de sinal
oposto com `reversesEntryId`.

**Consequências:**
- ✅ Trilha de auditoria completa e imutável
- ✅ Reconciliação é um recálculo determinístico (`SUM(amount)`)
- ✅ `balance = SUM(amount)` não pode ser implementado errado (débito já é negativo)
- ⚠️ Tabela cresce monotonicamente — exige particionamento por data em escala

---

### ADR-005 — Idempotência em duas camadas

**Status:** Aceito

**Contexto:** Retry de rede, mensagens reentregues e picos geram duplicidade.
Duplicar um lançamento é corromper o fluxo de caixa do comerciante.

**Decisão:**
- **Na entrada:** `Idempotency-Key` obrigatório, com índice único
  `(merchant_id, idempotency_key)`. Corrida resolvida capturando
  `unique_violation` e devolvendo o vencedor.
- **No consumo:** `processed_events` com `event_id` como PK, verificado na mesma
  transação do UPSERT.

**Consequências:**
- ✅ Duplicação impossível nas duas pontas
- ✅ Retry seguro em qualquer camada
- ⚠️ `processed_events` cresce — precisa de job de TTL (30 dias), registrado em
  Evoluções Futuras

---

### ADR-006 — Cache read-through com TTL de 30s

**Status:** Aceito

**Contexto:** RNF-04 exige p95 < 150 ms na consulta do consolidado.

**Decisão:** Redis com cache-aside, TTL de 30 segundos.

**Consequências:**
- ✅ p95 bem abaixo da meta para leituras repetidas
- ✅ Reduz carga no Postgres durante picos
- ⚠️ Staleness máxima de 30s — aceitável porque a consolidação já é eventual e
  nenhuma decisão financeira crítica depende de saldo em tempo real
- **Trade-off explícito:** consistência forte foi sacrificada por latência e
  disponibilidade. Se o negócio exigir leitura sempre atual, a estratégia é
  invalidação por evento no consumer, não TTL.

---

## 💵 Estimativa de Custos

Premissas: AWS `sa-east-1` (São Paulo), pico de 50 req/s, ~4,3M
lançamentos/mês, um cluster.

| Recurso | Spec | Mensal (USD) |
|---|---|---|
| EKS (control plane) | 1 cluster | 73 |
| Nodes (app) | 3 × t3.medium (2 vCPU / 4 GB) | 90 |
| RDS PostgreSQL | db.t4g.medium, Multi-AZ, 100 GB gp3 | 165 |
| ElastiCache Redis | cache.t4g.micro | 15 |
| Amazon MQ (RabbitMQ) | mq.t3.micro | 180 |
| ALB | 1 + LCU | 25 |
| NAT Gateway | 1 AZ | 35 |
| CloudWatch / logs | 20 GB | 15 |
| Secrets Manager | 10 secrets | 5 |
| Backup RDS | 100 GB | 3 |
| **Total infraestrutura** | | **≈ 606** |

**Licenças: USD 0.** Toda a stack é open source (PostgreSQL, RabbitMQ, Redis,
.NET, Prometheus, Grafana, MassTransit). Não há vendor lock-in — o custo é
integralmente de infraestrutura.

### Alternativas de redução de custo

| Otimização | Economia | Trade-off |
|---|---|---|
| RabbitMQ self-hosted em EKS (operador) | −USD 150/mês | Assume gestão do cluster |
| MSK Serverless em vez de Amazon MQ | −USD 60/mês | Mais setup, sem management UI |
| Aurora Serverless v2 em vez de RDS provisionado | Variável (~−USD 75/mês em carga média) | Custo imprevisível em picos intensos |
| Single-AZ em dev/staging | −USD 80/mês | Sem HA — nunca em produção |

---

## 🔄 Arquitetura de Transição

Cenário assumido: o comerciante hoje controla o fluxo em **planilha + sistema
legado de PDV** que escreve direto num banco consolidado mensal.

```
Fase 0 — Coexistência (semanas 1-4)
    Legado ──► adaptador CDC (Debezium lendo o binlog do legado)
                    │
                    └──► publica em cashflow.entry.registered (formato canônico)
                              │
                              └──► Consolidado novo roda em SHADOW MODE
                                   (calcula, compara com legado, NÃO serve tráfego)
                                   Divergências → tabela reconciliation_diffs

Fase 1 — Dual-write controlado (semanas 5-8)
    Cliente novo ──► Ledger API
    Legado       ──► CDC ──► mesmo tópico
    Consolidado serve leituras apenas para usuários piloto (feature flag por merchant)
    Reconciliação diária automática; divergência > 0,005 gera alerta

Fase 2 — Cutover por merchant (semanas 9-12)
    Migração em lotes (5% → 25% → 100%)
    Rollback = desligar feature flag, legado continua sendo fonte

Fase 3 — Descomissionamento
    Legado em read-only por 90 dias (exigência contábil)
    Depois: desligar
```

**Ponto crítico de segurança:** o CDC lê o banco legado em modo **read-only**.
A arquitetura nova **nunca escreve no legado** — elimina o risco de corromper
dados contábeis durante a transição. Se algo der errado, desligar a flag
significa que **nada aconteceu** no sistema antigo.

---

## 🛣️ Evoluções Futuras

Transparência sobre o que **não** está implementado nesta entrega:

### Curto prazo (bloqueiam produção)

1. **Testcontainers nos testes de integração.** Os testes atuais usam InMemory,
   que não executa o UPSERT nativo. Sem isso, o comportamento real do SQL não é
   verificado no CI.
2. **Autenticação JWT completa.** O código lê o claim `merchant_id` mas não
   configura `AddAuthentication/JwtBearer`. Falta o handler e um endpoint de
   token de desenvolvimento.
3. **Endpoint `POST /api/v1/balances/reconcile`.** Descrito na arquitetura e
   essencial para a recuperação, mas não implementado.

### Médio prazo (robustez)

4. **Job de TTL em `processed_events`** (30 dias) — cresce indefinidamente.
5. **Particionamento por `merchantId`** — hoje fila única; Kafka com
   `PartitionKey` garante ordenação por comerciante em escala.
6. **Rate limiting no gateway** — desenhado, não codificado. É o mecanismo que
   realmente garante o RNF-03 (rejeitar cedo > aceitar e perder).
7. **Teste de carga com k6/NBomber a 50 req/s** — provaria o RNF-02 com número,
   não com argumento. Faria isso antes de qualquer entrega real.

### Longo prazo (produto)

8. **Multi-moeda e multi-tenant com isolamento físico** — hoje single-tenant por
   serviço.
9. **Fechamento contábil mensal automatizado** com snapshot imutável.
10. **Event sourcing completo** — hoje outbox; a evolução natural é event store
    dedicado, permitindo projeções arbitrárias (fluxo por categoria, DRE, etc).
11. **API GraphQL/BFF** para dashboards do comerciante.

---

## 📄 Licença

MIT — este projeto é uma entrega de desafio técnico e pode ser usado como
referência de arquitetura.

---

## 👤 Autor

**Kleber Augusto** — Applied AI Engineer


📦 Arquivos complementares do repositório

.gitignore

bin/
obj/
*.user
*.suo
.vs/
.idea/
*.db
*.log
.env
appsettings.Development.local.json
TestResults/
coverage*.xml
coverage*.json
.DS_Store

deploy/init-db.sql

-- Schemas iniciais (as migrations do EF Core criam as tabelas)
CREATE DATABASE cashflow;
\c cashflow

-- Extensão para performance
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

-- Tabelas gerenciadas por EF Core Migrations.
-- Este arquivo existe apenas para garantir que o banco está pronto
-- antes das aplicações iniciarem em ambiente Docker.

docs/adr/001-microservices-with-outbox.md

# ADR-001 — Microsserviços com Outbox Pattern

## Status
Aceito — 29/10/2026

## Contexto

O sistema tem dois domínios com perfis de carga e requisitos de disponibilidade
distintos. O RNF-01 exige explicitamente:

> "O serviço de controle de lançamento não deve ficar indisponível se o
> sistema de consolidado diário cair."

Um desenho onde o Ledger chama o Consolidation via HTTP síncrono viola esse
requisito de forma direta: a indisponibilidade de um propaga para o outro.

## Decisão

Dois serviços independentes, cada um com seu banco, integrados por broker
(RabbitMQ) através do padrão **Transactional Outbox** no Ledger.

O agregado `Entry` e o registro do evento são persistidos na mesma transação
local. Um `BackgroundService` (`OutboxRelay`) lê os pendentes e publica no
broker. Falha de publicação não perde o evento.

## Alternativas consideradas

| Alternativa | Motivo da rejeição |
|---|---|
| Monolito modular | Não atende RNF-01 — um processo, uma falha |
| REST síncrono entre serviços | Acoplamento temporal, viola RNF-01 |
| Publicar direto no broker (sem outbox) | Janela de inconsistência entre commit e publish |
| Serverless | Cold start + conexões frias a banco a 50 req/s |

## Consequências

**Positivas:**
- Isolamento de falha real, verificável em teste (derrubar o consumer não afeta o Ledger)
- Replay e reconciliação nativos — o Consolidation é reconstruível do zero
- Consistência eventual com janela mensurável (centenas de ms em regime normal)
- Nenhum evento perdido em falha de broker (RPO ≈ 0)

**Negativas:**
- Complexidade operacional maior que monolito (mais um cluster, mais deploy)
- Cliente vê o saldo com a latência de consolidação — aceitável para contexto diário
- `outbox_messages` precisa de limpeza periódica (não implementado ainda)

## Verificação

O requisito é validado pelo teste de aceitação:
1. `docker compose stop consolidation-api`
2. `POST /api/v1/entries` → deve retornar `201`
3. `docker compose start consolidation-api`
4. `GET /api/v1/balances/{date}` → deve refletir o lançamento

docs/adr/005-idempotency-strategy.md

# ADR-005 — Idempotência em duas camadas

## Status
Aceito — 29/10/2026

## Contexto

Um lançamento duplicado corrompe o fluxo de caixa do comerciante. Duplicidade
pode surgir de: retry de rede do cliente, redelivery do broker, e condições de
corrida em picos de 50 req/s.

## Decisão

Duas camadas independentes de proteção:

### Camada 1 — Entrada (Ledger)

- Header `Idempotency-Key` **obrigatório** em `POST /entries` (400 se ausente)
- Índice único no banco: `UNIQUE (merchant_id, idempotency_key)`
- Fluxo: busca por chave → se existe, retorna o original (`200`)
- Corrida: duas requisições idênticas simultâneas → uma vence o INSERT, a outra
  captura `unique_violation` (Postgres `23505`) e retorna o vencedor

### Camada 2 — Consumo (Consolidation)

- Tabela `processed_events` com `event_id` como PK
- Verificação de existência **antes** do UPSERT
- Insert do `processed_event` na **mesma transação** do UPSERT

## Por que duas camadas

A camada 1 protege contra duplicidade **na origem** (cliente reenviando).
A camada 2 protege contra duplicidade **no transporte** (broker reentregando).

Nenhuma das duas substitui a outra: um evento pode ser reentregue pelo broker
mesmo que o cliente tenha enviado uma única vez.

## Consequências

**Positivas:**
- Duplicação impossível nas duas pontas
- Retry seguro em qualquer camada (cliente, relay, consumer)
- Auditoria limpa

**Negativas:**
- `processed_events` cresce sem limite — requer job de TTL (pendente)
- O(br) na busca por `idempotency_key` em picos — mitigado por índice único composto


Está tudo em markdown puro, pronto para colar no GitHub. Se quiser, posso agora gerar os arquivos que faltam (docs/architecture.md, docs/requirements.md, docs/transition-architecture.md, docs/observability.md, docs/costs.md e os ADRs 002/003/004/006) completos — cada um como bloco de código separado para você commitar direto.