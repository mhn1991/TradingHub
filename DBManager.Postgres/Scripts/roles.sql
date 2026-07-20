-- Section 25: separate PostgreSQL roles for TradingHub.
-- Run once per PostgreSQL instance by an administrator, outside the EF Core migration path.
-- Passwords are placeholders; replace before granting production credentials.

CREATE ROLE trading_migrator  LOGIN PASSWORD 'CHANGE_ME' CREATEDB;
CREATE ROLE trading_runtime   LOGIN PASSWORD 'CHANGE_ME';
CREATE ROLE trading_reader    LOGIN PASSWORD 'CHANGE_ME';
CREATE ROLE trading_research  LOGIN PASSWORD 'CHANGE_ME';
CREATE ROLE trading_backup    LOGIN PASSWORD 'CHANGE_ME';

-- trading_migrator: owns schema/table/index/partition DDL and runs EF Core migrations.
GRANT ALL PRIVILEGES ON DATABASE tradinghub TO trading_migrator;

-- Section 5: the eleven logical schemas exist from Phase 0 onward, owned by trading_migrator,
-- even before later phases populate them with tables.
CREATE SCHEMA IF NOT EXISTS reference   AUTHORIZATION trading_migrator;
CREATE SCHEMA IF NOT EXISTS config      AUTHORIZATION trading_migrator;
CREATE SCHEMA IF NOT EXISTS runtime     AUTHORIZATION trading_migrator;
CREATE SCHEMA IF NOT EXISTS decision    AUTHORIZATION trading_migrator;
CREATE SCHEMA IF NOT EXISTS risk        AUTHORIZATION trading_migrator;
CREATE SCHEMA IF NOT EXISTS execution   AUTHORIZATION trading_migrator;
CREATE SCHEMA IF NOT EXISTS management  AUTHORIZATION trading_migrator;
CREATE SCHEMA IF NOT EXISTS operations  AUTHORIZATION trading_migrator;
CREATE SCHEMA IF NOT EXISTS research    AUTHORIZATION trading_migrator;
CREATE SCHEMA IF NOT EXISTS analytics   AUTHORIZATION trading_migrator;
CREATE SCHEMA IF NOT EXISTS integration AUTHORIZATION trading_migrator;
CREATE SCHEMA IF NOT EXISTS security    AUTHORIZATION trading_migrator;

-- If `operations` and its migrations-history table were bootstrapped earlier under a different
-- owner (e.g. a single dev superuser before roles existed), reassign them to trading_migrator.
ALTER SCHEMA operations OWNER TO trading_migrator;
ALTER TABLE IF EXISTS operations.schema_history OWNER TO trading_migrator;

-- trading_runtime (section 25.1): read/write runtime, decision, risk, execution, management,
-- operations tables; read approved configuration and encrypted credential payloads. The
-- application-layer key is never stored in PostgreSQL. No DDL and no grants.
GRANT USAGE ON SCHEMA
    reference, config, runtime, decision, risk, execution, management, operations, integration
    TO trading_runtime;
GRANT USAGE ON SCHEMA security TO trading_runtime;
GRANT SELECT ON ALL TABLES IN SCHEMA security TO trading_runtime;
GRANT SELECT ON ALL TABLES IN SCHEMA reference TO trading_runtime;
GRANT SELECT ON ALL TABLES IN SCHEMA config TO trading_runtime;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA
    runtime, decision, risk, execution, management, operations, integration
    TO trading_runtime;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA
    runtime, decision, risk, execution, management, operations, integration
    TO trading_runtime;

-- trading_reader (section 25.2): read-only for Dashboard/monitoring.
GRANT USAGE ON SCHEMA
    reference, config, runtime, decision, risk, execution, management, operations, analytics
    TO trading_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA
    reference, config, runtime, decision, risk, execution, management, operations, analytics
    TO trading_reader;

-- trading_research: research/analytics bulk import, isolated from the critical live pool.
GRANT USAGE ON SCHEMA research, analytics, config TO trading_research;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA research, analytics TO trading_research;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA research, analytics TO trading_research;
GRANT SELECT ON ALL TABLES IN SCHEMA config TO trading_research;

-- trading_backup: used only by pg_dump/pg_basebackup tooling.
GRANT pg_read_all_data TO trading_backup;

-- Default privileges: tables/sequences created later by trading_migrator (EF Core migrations)
-- automatically pick up the same grants above, without re-running this script every phase.
ALTER DEFAULT PRIVILEGES FOR ROLE trading_migrator IN SCHEMA reference, config
    GRANT SELECT ON TABLES TO trading_runtime, trading_reader;
ALTER DEFAULT PRIVILEGES FOR ROLE trading_migrator IN SCHEMA
    runtime, decision, risk, execution, management, operations, integration
    GRANT SELECT, INSERT, UPDATE ON TABLES TO trading_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE trading_migrator IN SCHEMA
    runtime, decision, risk, execution, management, operations, integration
    GRANT USAGE ON SEQUENCES TO trading_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE trading_migrator IN SCHEMA
    reference, config, runtime, decision, risk, execution, management, operations, analytics
    GRANT SELECT ON TABLES TO trading_reader;
ALTER DEFAULT PRIVILEGES FOR ROLE trading_migrator IN SCHEMA research, analytics
    GRANT SELECT, INSERT, UPDATE ON TABLES TO trading_research;
ALTER DEFAULT PRIVILEGES FOR ROLE trading_migrator IN SCHEMA research, analytics
    GRANT USAGE ON SEQUENCES TO trading_research;
ALTER DEFAULT PRIVILEGES FOR ROLE trading_migrator IN SCHEMA security
    GRANT SELECT ON TABLES TO trading_runtime;
