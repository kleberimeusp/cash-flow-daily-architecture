# Observabilidade

Stack: **OpenTelemetry + Prometheus + Grafana** (RNF-07), sem lock-in de vendor.

## Métricas expostas (`/metrics`)

Ambas as APIs expõem `/metrics` no formato Prometheus (exportador OTel).

**Negócio:**

| Métrica | Tipo | Serviço | Significado |
|---|---|---|---|
| `cashflow_entries_registered_total{merchant,type}` | counter | Ledger | Lançamentos registrados (type: credit/debit/reversal) |
| `cashflow_outbox_pending_count` | gauge | Ledger | Mensagens pendentes no outbox |
| `cashflow_outbox_publish_duration_seconds` | histogram | Ledger | Duração da publicação no broker |
| `cashflow_consumer_lag` | gauge | Consolidation | Mensagens pendentes na fila principal |
| `cashflow_consolidation_lag_seconds` | histogram | Consolidation | Atraso registro → consolidação por evento |
| `cashflow_balance_consolidated_total{merchant}` | counter | Consolidation | Eventos consolidados |
| `cashflow_dlq_depth` | gauge | Consolidation | Mensagens na DLQ |

**Técnicas:** latência HTTP p95/p99 por endpoint, taxa de erro 4xx/5xx, runtime
.NET (GC, thread pool) — via instrumentação ASP.NET Core + Runtime do OTel.

## SLIs e SLOs

| SLI | SLO | Alerta |
|---|---|---|
| Disponibilidade Ledger | 99,95% / 30 dias | Error budget < 20% |
| Latência p95 `POST /entries` | < 200 ms | > 300 ms por 5 min |
| Lag de consolidação | < 5 s p95 | > 30 s por 2 min |
| Perda em pico | ≤ 5% | > 3% |
| Profundidade da DLQ | 0 | **qualquer valor > 0** |

## Alertas que realmente importam

1. **`cashflow_outbox_pending_count > 1000` por 5 min** — broker lento ou fora.
   É o sinal mais precoce de problema de acoplamento.
2. **`cashflow_dlq_depth > 0`** — houve evento que não pôde ser processado.
   Requer intervenção humana; nunca deve ser ignorado.
3. **`cashflow_consumer_lag > 30s`** — consolidação atrasando. Se crescer
   indefinidamente, o RNF-02 está em risco.

## Tracing

`correlationId` propaga do cliente → Ledger → outbox → broker → consumer →
banco. MassTransit injeta contexto automaticamente (source `MassTransit`); o
header `X-Correlation-Id` é aceito na entrada e devolvido na resposta. Um único
trace mostra a jornada completa do lançamento.

Exportação OTLP é ativada quando `OTEL_EXPORTER_OTLP_ENDPOINT` está configurada
(opcional; sem collector, as métricas continuam disponíveis em `/metrics`).

## Health checks

| Endpoint | Verifica |
|---|---|
| `GET /health` (Ledger) | Postgres + RabbitMQ |
| `GET /health` (Consolidation) | Postgres + RabbitMQ + Redis |

## Dashboard Grafana sugerido

Quatro painéis: volume de lançamentos (série temporal), lag de consolidação
(gauge), outbox pendente (série), erros por tipo (barras). O datasource
Prometheus é provisionado automaticamente no compose.
