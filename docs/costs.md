# Estimativa de Custos

Premissas: AWS `sa-east-1` (São Paulo), pico de 50 req/s, ~4,3M
lançamentos/mês, um cluster.

| Recurso | Spec | Mensal (USD) |
|---|---|---|
| EKS (control plane) | 1 cluster | 73 |
| Nodes (app) | 3 × t3.medium (2 vCPU / 4 GB) | 90 |
| RDS PostgreSQL | db.t4g.medium, Multi-AZ, 100 GB gp3 | 165 |
| ElastiCache Redis | cache.t4g.micro | 15 |
| Amazon MQ (RabbitMQ) | mq.t3.micro | 180 |
| ALB | 1 + LCU | 25 |
| NAT Gateway | 1 AZ | 35 |
| CloudWatch / logs | 20 GB | 15 |
| Secrets Manager | 10 secrets | 5 |
| Backup RDS | 100 GB | 3 |
| **Total infraestrutura** | | **≈ 606** |

**Licenças: USD 0.** Toda a stack é open source (PostgreSQL, RabbitMQ, Redis,
.NET, Prometheus, Grafana, MassTransit). Não há vendor lock-in — o custo é
integralmente de infraestrutura.

## Alternativas de redução de custo

| Otimização | Economia | Trade-off |
|---|---|---|
| RabbitMQ self-hosted em EKS (operador) | −USD 150/mês | Assume gestão do cluster |
| MSK Serverless em vez de Amazon MQ | −USD 60/mês | Mais setup, sem management UI |
| Aurora Serverless v2 em vez de RDS provisionado | Variável (~−USD 75/mês em carga média) | Custo imprevisível em picos intensos |
| Single-AZ em dev/staging | −USD 80/mês | Sem HA — nunca em produção |
