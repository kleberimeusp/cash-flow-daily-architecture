# Requisitos

## Funcionais

| ID | Requisito | Serviço | Implementação |
|---|---|---|---|
| RF-01 | Registrar lançamento (débito/crédito) | Ledger | `POST /api/v1/entries` |
| RF-02 | Consultar lançamento por id | Ledger | `GET /api/v1/entries/{id}` |
| RF-03 | Listar lançamentos por período | Ledger | `GET /api/v1/entries?from=&to=` |
| RF-04 | Estornar lançamento | Ledger | `POST /api/v1/entries/{id}/reverse` |
| RF-05 | Publicar evento `EntryRegistered` | Ledger | Outbox + `OutboxRelay` |
| RF-06 | Manter saldo diário por comerciante | Consolidation | `EntryConsumer` + UPSERT |
| RF-07 | Consultar consolidado de um dia | Consolidation | `GET /api/v1/balances/{date}` |
| RF-08 | Consultar consolidado por intervalo | Consolidation | `GET /api/v1/balances?from=&to=` |
| RF-09 | Reprocessar período (reconciliação) | Consolidation | ⏳ Evolução de curto prazo |
| RF-10 | Idempotência na recepção de eventos | Consolidation | `processed_events` (PK `event_id`) |

## Não funcionais

| ID | Requisito | Meta | Mecanismo |
|---|---|---|---|
| RNF-01 | Lançamentos disponível com Consolidado fora | 99,95% | Broker desacoplado + outbox |
| RNF-02 | Consolidado suporta pico | 50 req/s | Prefetch 32 + 4 consumidores |
| RNF-03 | Perda máxima no pico | ≤ 5% | Retry exponencial + DLQ + backpressure |
| RNF-04 | Latência consulta consolidado | p95 < 150 ms | Redis + índice composto |
| RNF-05 | Latência escrita lançamento | p95 < 200 ms | Escrita local + outbox |
| RNF-06 | Segurança no consumo | — | JWT RS256 + claim `merchant_id` + mTLS ⏳ |
| RNF-07 | Observabilidade | — | OpenTelemetry + Prometheus + Grafana |
| RNF-08 | Recuperação de falha | RPO ≈ 0, RTO < 5 min | Outbox + replicação Postgres |
| RNF-09 | Auditabilidade | 100% | Event log imutável (append-only) |

⏳ = parcial nesta entrega: o código lê o claim `merchant_id`, mas
`AddAuthentication/JwtBearer` não está configurado (ver README → Evoluções
Futuras).

## Regras de negócio

Ver [domain-model.md](./domain-model.md#regras-de-negócio) — RN-01 a RN-06.

## Critérios de aceitação

1. **Derrubar o Consolidation** e registrar um lançamento retorna `201` — o
   Ledger não foi afetado.
2. **Reiniciar o Consolidation** faz o lançamento aparecer no saldo consolidado.
3. **Repetir o mesmo `POST /entries`** com a mesma `Idempotency-Key` retorna o
   lançamento original, sem duplicar.
4. **Reentregar o mesmo evento** ao consumer não duplica o valor no saldo.
5. **`ON CONFLICT DO UPDATE`** sustenta 50 escritas concorrentes no mesmo dia
   sem deadlock.
6. **Estornar** cria um novo lançamento e o original permanece intacto.

### Teste de aceitação do requisito crítico

```bash
docker compose -f deploy/docker-compose.yml stop consolidation-api
curl -X POST http://localhost:5081/api/v1/entries \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"amount": 100.00, "occurredOn": "2026-10-29", "description": "Teste"}'
# Esperado: 201 Created

docker compose -f deploy/docker-compose.yml start consolidation-api
sleep 2
curl http://localhost:5082/api/v1/balances/2026-10-29
# Esperado: balance = 100.00
```
