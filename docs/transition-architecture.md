# Arquitetura de Transição

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
