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
