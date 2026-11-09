-- Provisiona os papéis de banco de Billing e Execução para a prova ServiceDatabaseIsolation.
-- Os papéis são criados sem nenhum privilégio nas tabelas do OS Service e sem senha versionada.
-- Uso (conectado ao banco do OS com um papel administrador de teste):
--   psql -v ON_ERROR_STOP=1 -v billing_password=... -v execucao_password=... -f provisionar-papeis-servicos.sql
-- Papéis criados: autoreparos_billing e autoreparos_execucao.

SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', 'autoreparos_billing', :'billing_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'autoreparos_billing') \gexec

SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', 'autoreparos_execucao', :'execucao_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'autoreparos_execucao') \gexec

REVOKE ALL ON ALL TABLES IN SCHEMA public FROM autoreparos_billing, autoreparos_execucao;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM autoreparos_billing, autoreparos_execucao;
REVOKE ALL ON SCHEMA public FROM autoreparos_billing, autoreparos_execucao;

SELECT format('REVOKE ALL ON DATABASE %I FROM autoreparos_billing, autoreparos_execucao', current_database()) \gexec

ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM autoreparos_billing, autoreparos_execucao;
