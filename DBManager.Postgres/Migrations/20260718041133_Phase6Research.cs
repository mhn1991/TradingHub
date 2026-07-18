using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DBManager.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Phase6Research : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "research");

            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.CreateTable(
                name: "execution_quality",
                schema: "analytics",
                columns: table => new
                {
                    execution_quality_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_bid = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    decision_ask = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    submission_bid = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    submission_ask = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    expected_fill_price = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    actual_fill_price = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    expected_spread = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    actual_spread = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    expected_slippage = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    actual_slippage = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    submission_latency_ms = table.Column<int>(type: "integer", nullable: false),
                    ack_latency_ms = table.Column<int>(type: "integer", nullable: true),
                    fill_latency_ms = table.Column<int>(type: "integer", nullable: true),
                    session = table.Column<short>(type: "smallint", nullable: false),
                    regime = table.Column<short>(type: "smallint", nullable: false),
                    calculated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_execution_quality", x => x.execution_quality_id);
                });

            migrationBuilder.CreateTable(
                name: "model_monitoring_windows",
                schema: "analytics",
                columns: table => new
                {
                    window_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    sample_count = table.Column<long>(type: "bigint", nullable: false),
                    drift_metrics = table.Column<string>(type: "jsonb", nullable: false),
                    calibration_metrics = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_monitoring_windows", x => x.window_id);
                });

            migrationBuilder.CreateTable(
                name: "research_runs",
                schema: "research",
                columns: table => new
                {
                    research_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_type = table.Column<short>(type: "smallint", nullable: false),
                    strategy_id = table.Column<string>(type: "text", nullable: false),
                    strategy_version = table.Column<string>(type: "text", nullable: false),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    dataset_hash = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    random_seed = table.Column<long>(type: "bigint", nullable: true),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    summary_metrics = table.Column<string>(type: "jsonb", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_research_runs", x => x.research_run_id);
                });

            migrationBuilder.CreateTable(
                name: "calibration_runs",
                schema: "research",
                columns: table => new
                {
                    calibration_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    research_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<short>(type: "smallint", nullable: false),
                    input_artifact_ids = table.Column<string>(type: "jsonb", nullable: false),
                    output_artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    metrics = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calibration_runs", x => x.calibration_run_id);
                    table.ForeignKey(
                        name: "fk_calibration_runs_calibration_artifacts_output_artifact_id",
                        column: x => x.output_artifact_id,
                        principalSchema: "config",
                        principalTable: "calibration_artifacts",
                        principalColumn: "artifact_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_calibration_runs_research_runs_research_run_id",
                        column: x => x.research_run_id,
                        principalSchema: "research",
                        principalTable: "research_runs",
                        principalColumn: "research_run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "time_series_folds",
                schema: "research",
                columns: table => new
                {
                    fold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    research_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fold_number = table.Column<int>(type: "integer", nullable: false),
                    training_from = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    training_to = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    validation_from = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    validation_to = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    test_from = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    test_to = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    purge_duration = table.Column<TimeSpan>(type: "interval", nullable: false),
                    embargo_duration = table.Column<TimeSpan>(type: "interval", nullable: false),
                    sample_counts = table.Column<string>(type: "jsonb", nullable: false),
                    metrics = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_time_series_folds", x => x.fold_id);
                    table.ForeignKey(
                        name: "fk_time_series_folds_research_runs_research_run_id",
                        column: x => x.research_run_id,
                        principalSchema: "research",
                        principalTable: "research_runs",
                        principalColumn: "research_run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calibration_runs_output_artifact_id",
                schema: "research",
                table: "calibration_runs",
                column: "output_artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_calibration_runs_research_run_id",
                schema: "research",
                table: "calibration_runs",
                column: "research_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_execution_quality_order_id",
                schema: "analytics",
                table: "execution_quality",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_model_monitoring_windows_artifact_id_window_start",
                schema: "analytics",
                table: "model_monitoring_windows",
                columns: new[] { "artifact_id", "window_start" });

            migrationBuilder.CreateIndex(
                name: "ix_time_series_folds_research_run_id_fold_number",
                schema: "research",
                table: "time_series_folds",
                columns: new[] { "research_run_id", "fold_number" },
                unique: true);

            // analytics.candidate_outcomes is created here as raw SQL, not via CreateTable, because
            // it is a genuine PostgreSQL declarative-partitioned table (section 20) and EF Core has
            // no first-class support for that. The C# entity is mapped with ExcludeFromMigrations so
            // LINQ reads still work; this SQL is the only place the physical table is defined.
            migrationBuilder.Sql(
                """
                CREATE TABLE analytics.candidate_outcomes (
                    outcome_id uuid NOT NULL,
                    candidate_id uuid NOT NULL,
                    decision_time timestamptz NOT NULL,
                    horizon_end timestamptz NOT NULL,
                    target_reached boolean NOT NULL,
                    stop_reached boolean NOT NULL,
                    first_terminal_event smallint NOT NULL,
                    mfe_price numeric(28,12) NOT NULL,
                    mae_price numeric(28,12) NOT NULL,
                    mfe_r numeric(18,10) NOT NULL,
                    mae_r numeric(18,10) NOT NULL,
                    maximum_achievable_r numeric(18,10) NOT NULL,
                    time_to_mfe interval NOT NULL,
                    time_to_mae interval NOT NULL,
                    spread_adjusted_r numeric(18,10) NOT NULL,
                    outcome_version integer NOT NULL,
                    calculated_at timestamptz NOT NULL,
                    PRIMARY KEY (decision_time, outcome_id)
                ) PARTITION BY RANGE (decision_time);

                -- Partition keys must be part of any unique constraint on a partitioned table, so this
                -- (decision_time, candidate_id) index is the idempotency key COPY/upsert callers use
                -- instead of a bare "candidate_id UNIQUE" (not achievable across partitions).
                CREATE UNIQUE INDEX ix_candidate_outcomes_decision_time_candidate_id
                    ON analytics.candidate_outcomes (decision_time, candidate_id);
                CREATE INDEX ix_candidate_outcomes_candidate_id
                    ON analytics.candidate_outcomes (candidate_id);

                -- Section 20.1: create partitions ahead of need. Two concrete monthly partitions
                -- bracketing initial deployment, plus a DEFAULT catch-all so inserts never fail and
                -- rows landing there are detectable (section 20.1: "detect rows in a default partition").
                CREATE TABLE analytics.candidate_outcomes_2026_07 PARTITION OF analytics.candidate_outcomes
                    FOR VALUES FROM ('2026-07-01T00:00:00Z') TO ('2026-08-01T00:00:00Z');
                CREATE TABLE analytics.candidate_outcomes_2026_08 PARTITION OF analytics.candidate_outcomes
                    FOR VALUES FROM ('2026-08-01T00:00:00Z') TO ('2026-09-01T00:00:00Z');
                CREATE TABLE analytics.candidate_outcomes_default PARTITION OF analytics.candidate_outcomes
                    DEFAULT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS analytics.candidate_outcomes CASCADE;");

            migrationBuilder.DropTable(
                name: "calibration_runs",
                schema: "research");

            migrationBuilder.DropTable(
                name: "execution_quality",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "model_monitoring_windows",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "time_series_folds",
                schema: "research");

            migrationBuilder.DropTable(
                name: "research_runs",
                schema: "research");
        }
    }
}
