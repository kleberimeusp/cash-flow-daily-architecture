# ADR-006 — Cache read-through com TTL de 30s

## Status
Aceito — 29/10/2026

## Contexto

RNF-04 exige p95 < 150 ms na consulta do consolidado. O saldo já é
eventualmente consistente por construção (consolidação assíncrona), então um
staleness adicional pequeno e limitado é aceitável.

## Decisão

Redis com cache-aside (read-through) na consulta do consolidado:

```
GET /balances/{date} → Redis (hit? retorna) → miss → Postgres → popula cache (TTL 30s)
```

- TTL de **30 segundos** limita o staleness.
- O consumer **invalida a chave** do `(merchant, date)` afetado a cada evento
  consolidado — na prática o staleness tende a zero enquanto há movimentação.
- Falha de Redis degrada para leitura direta no Postgres — cache nunca derruba
  a consulta.

## Alternativas consideradas

| Alternativa | Motivo da rejeição |
|---|---|
| Sem cache | p95 depende do Postgres sob pico de leitura; desperdiça a natureza repetitiva da consulta |
| Cache com invalidação apenas por evento (sem TTL) | Chave órfã (evento perdido/DLQ) ficaria velha para sempre; TTL é o backstop |
| Cache na escrita (write-through) | Acopla consumer ao Redis no caminho crítico de consolidação |

## Consequências

**Positivas:**
- p95 bem abaixo da meta para leituras repetidas
- Reduz carga no Postgres durante picos

**Negativas:**
- Staleness máxima de 30s — aceitável porque a consolidação já é eventual e
  nenhuma decisão financeira crítica depende de saldo em tempo real

**Trade-off explícito:** consistência forte foi sacrificada por latência e
disponibilidade. Se o negócio exigir leitura sempre atual, a estratégia é
invalidação por evento no consumer, não TTL.
