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
- Overhead na busca por `idempotency_key` em picos — mitigado por índice único composto
