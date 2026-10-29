-- Bancos separados por serviço (ADR-001/ADR-003). As tabelas são criadas
-- pelas migrations do EF Core na subida de cada API; este arquivo garante que
-- os bancos existam antes das aplicações iniciarem em ambiente Docker.

CREATE DATABASE cashflow_ledger;
CREATE DATABASE cashflow_consolidation;

\c cashflow_ledger
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

\c cashflow_consolidation
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
