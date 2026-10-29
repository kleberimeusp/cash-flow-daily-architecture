# ADR-004 — Ledger append-only com estorno

## Status
Aceito — 29/10/2026

## Contexto

Sistema financeiro exige auditabilidade (RNF-09). Alterar ou apagar
lançamentos destrói a trilha e inviabiliza reconciliação determinística.

## Decisão

Lançamentos são **imutáveis** (RN-01): não existe UPDATE nem DELETE em
`entries`. Estorno é um **novo lançamento de sinal oposto** com
`reversesEntryId` apontando para o original, na mesma data de competência
(RN-02) — o estorno zera o original no próprio dia.

Combinado com RN-03 (débito = valor negativo, sem enum), o saldo é sempre
`SUM(amount)` — não há como implementar errado.

## Alternativas consideradas

| Alternativa | Motivo da rejeição |
|---|---|
| UPDATE com flag `cancelled` | Destrói a trilha; reconciliação deixa de ser `SUM(amount)` |
| DELETE lógico (soft delete) | Mesmo problema; auditoria exige o registro original intacto |
| Event sourcing completo | Correto na direção, mas complexidade desproporcional ao escopo — registrado como evolução de longo prazo |

## Consequências

**Positivas:**
- Trilha de auditoria completa e imutável
- Reconciliação é um recálculo determinístico (`SUM(amount)`)
- `balance = SUM(amount)` não pode ser implementado errado (débito já é negativo)

**Negativas:**
- Tabela cresce monotonicamente — exige particionamento por data em escala
