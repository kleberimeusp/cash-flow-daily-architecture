# Modelo de Domínio

## Bounded Contexts

| Contexto | Tipo | Agregado raiz | Responsabilidade |
|---|---|---|---|
| **Ledger** | Core | `Entry` | Verdade absoluta das movimentações |
| **Consolidation** | Core | `DailyBalance` | Projeção derivada, reconstruível |
| **Identity** | Generic | `Merchant` | Tenancy e autorização |

O Consolidation **nunca** é fonte da verdade. É integralmente reconstruível a
partir do Ledger — é isso que torna o desacoplamento de disponibilidade
possível e a reconciliação viável.

## Linguagem ubíqua

| Termo | Definição |
|---|---|
| `Entry` | Lançamento imutável. Positivo = crédito, negativo = débito |
| `Reversal` | Entry que anula outra via `reversesEntryId` |
| `DailyBalance` | Soma líquida das entries de um comerciante em um dia |
| `CorrelationId` | Rastreia a requisição por todo o sistema |
| `IdempotencyKey` | Impede duplicidade de um mesmo lançamento |

## Regras de negócio

| ID | Regra | Justificativa |
|---|---|---|
| **RN-01** | Lançamentos são append-only; estorno é novo lançamento de sinal oposto | Trilha de auditoria imutável; reconciliação determinística |
| **RN-02** | Consolida pela data de competência (`occurredOn`), não pela data de processamento | Pico às 23:59 não deve jogar lançamento para o dia seguinte |
| **RN-03** | Débito é valor negativo; não existe enum `Debit`/`Credit` | `saldo = SUM(amount)` não pode ser implementado errado |
| **RN-04** | Arredondamento com `MidpointRounding.ToEven` (2 casas) | Evita viés sistemático que aparece como divergência de centavos |
| **RN-05** | `Idempotency-Key` obrigatório na criação | Retry de rede não pode duplicar lançamento |
| **RN-06** | Tenant sempre vem do token, nunca do corpo da requisição | Impede IDOR entre comerciantes |

## Agregados

### `Entry` (Ledger)

```csharp
Entry.Create(merchantId, amount, occurredOn, description, idempotencyKey, correlationId, today)
entry.CreateReversal(idempotencyKey, correlationId, today)
```

- Imutável após criação; sem UPDATE nem DELETE (RN-01).
- `amount` é arredondado com `MidpointRounding.ToEven` em 2 casas (RN-04) e
  não pode ser zero (nem antes nem depois do arredondamento).
- `occurredOn` não pode ser futura.
- Estorno: novo `Entry` com `-amount`, mesma `occurredOn` (zera o original no
  próprio dia) e `reversesEntryId` apontando para o original. Estornar um
  estorno é rejeitado com `409`.

### `DailyBalance` (Consolidation)

- Chave composta `(merchant_id, date)`.
- Atualizado exclusivamente por UPSERT atômico (`ON CONFLICT DO UPDATE`) no
  consumo de `EntryRegisteredEvent`.
- `balance = SUM(amount)` do dia; `totalCredits`/`totalDebits` guardam as
  parcelas para o relatório.

### `ProcessedEvent` (Consolidation)

- `event_id` como PK. Inserido na mesma transação do UPSERT: reentrega do
  broker vira violação de unicidade e é absorvida (ADR-005, camada 2).
