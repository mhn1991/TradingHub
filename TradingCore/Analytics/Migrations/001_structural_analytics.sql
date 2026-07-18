CREATE SCHEMA IF NOT EXISTS analytics;

-- All lifecycle, relationship, and outcome tables share the same causal envelope. The complete
-- immutable contract remains in payload; promoted fields are indexed for attribution/replay.
DO $migration$
DECLARE
    table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'supply_demand_zones',
        'supply_demand_zone_events',
        'zone_candidate_relationships',
        'zone_trade_relationships',
        'zone_outcomes',
        'liquidity_pools',
        'liquidity_pool_events',
        'liquidity_sweeps',
        'liquidity_candidate_relationships',
        'liquidity_trade_relationships',
        'liquidity_outcomes'
    ]
    LOOP
        EXECUTE format($ddl$
            CREATE TABLE IF NOT EXISTS analytics.%I (
                stable_id uuid NOT NULL,
                instrument text NOT NULL,
                interval_name text NOT NULL,
                profile_hash text NOT NULL,
                originated_at timestamptz NOT NULL,
                confirmed_at timestamptz NOT NULL,
                available_at timestamptz NOT NULL,
                snapshot_version bigint NOT NULL,
                state text NOT NULL,
                related_entity_id uuid NULL,
                candidate_id uuid NULL,
                trade_id uuid NULL,
                payload jsonb NOT NULL,
                recorded_at timestamptz NOT NULL,
                PRIMARY KEY (stable_id, snapshot_version),
                CHECK (confirmed_at >= originated_at),
                CHECK (available_at >= confirmed_at)
            )
        $ddl$, table_name);
        EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON analytics.%I (instrument, interval_name, profile_hash, available_at)', table_name || '_causal_idx', table_name);
        EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON analytics.%I (related_entity_id)', table_name || '_related_idx', table_name);
        EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON analytics.%I (candidate_id, trade_id)', table_name || '_lineage_idx', table_name);
    END LOOP;
END
$migration$;
