# ADR-003 — PostgreSQL como banco de ambos os serviços

## Status
Aceito — 29/10/2026

## Contexto

Os dois serviços precisam de ACID, UPSERT atômico, índice parcial e baixo
custo. O dimensionamento (RNF-02/RNF-03) depende de resolver contenção de
escrita concorrente no mesmo `(merchant_id, date)` sem deadlock.

## Decisão

PostgreSQL 16 para os dois serviços, com **bancos separados**
(`cashflow_ledger` e `cashflow_consolidation`) — um serviço nunca lê o banco
do outro.

Recursos decisivos:

- `ON CONFLICT DO UPDATE` — UPSERT atômico do saldo diário; essencial para
  50 req/s no mesmo dia sem deadlock.
- `jsonb` — payload do outbox consultável.
- Índice parcial `WHERE published_at IS NULL` — o poll do relay só varre
  mensagens pendentes.
- `FOR UPDATE SKIP LOCKED` — múltiplas instâncias do relay sem bloqueio mútuo.
- `DateOnly` → `date` nativo para a data de competência.

## Alternativas consideradas

| Alternativa | Motivo da rejeição |
|---|---|
| SQL Server | Custo de licença; sem benefício técnico proporcional |
| MySQL | UPSERT (`ON DUPLICATE KEY`) menos expressivo; sem índice parcial |
| MongoDB | Sem transações multi-documento equivalentes para o outbox na mesma transação do agregado |
| Bancos diferentes por serviço (poliglota) | Complexidade operacional sem ganho neste escopo |

## Consequências

**Positivas:**
- `ON CONFLICT DO UPDATE` atômico — essencial para 50 req/s sem deadlock
- `jsonb` para payload do outbox + índice parcial `WHERE published_at IS NULL`
- Custo zero de licença, Multi-AZ gerenciado no RDS

**Negativas:**
- Escala vertical limitada — sharding por `merchantId` é a evolução natural
