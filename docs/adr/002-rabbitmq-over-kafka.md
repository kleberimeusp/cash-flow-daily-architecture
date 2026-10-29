# ADR-002 — RabbitMQ em vez de Kafka

## Status
Aceito — 29/10/2026

## Contexto

Volume previsto de ~50 msg/s no pico (~50 KB/s), uma fila lógica
(`cashflow.entry.registered`), necessidade de DLQ robusta e baixo custo
operacional. A força dominante é a **proporcionalidade**: a mensageria deve ser
tão simples quanto o volume permite.

## Decisão

RabbitMQ 3.13 com MassTransit como cliente de mensageria.

- DLQ nativa (`cashflow.entry.registered.dlq`) e management UI sem custo extra.
- MassTransit fornece retry exponencial, redelivery, backpressure
  (prefetch/concorrência) e instrumentação OpenTelemetry prontos.

## Alternativas consideradas

| Alternativa | Motivo da rejeição |
|---|---|
| Kafka | Overkill para 50 msg/s: cluster, ZooKeeper/KRaft e operação muito mais caros, sem benefício proporcional |
| SQS/SNS (gerenciado) | Lock-in de cloud; dificulta rodar local com Docker Compose |
| Publicar/consumir com RabbitMQ.Client puro | Reinventar retry, DLQ e observabilidade que o MassTransit já dá |

## Consequências

**Positivas:**
- DLQ nativa e simples, management UI, menor curva operacional
- Custo AWS via Amazon MQ previsível (~USD 180/mês)
- MassTransit permite trocar transporte (RabbitMQ → Kafka) sem reescrever a aplicação

**Negativas:**
- Não escala para centenas de milhares de msg/s — ponto de virada documentado
  em 5.000 msg/s ou necessidade de replay histórico em massa
- Ordenação por comerciante exige particionamento; hoje fila única
