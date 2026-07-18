using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DBManager.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Phase2Decision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "decision");

            migrationBuilder.CreateTable(
                name: "agent_evaluations",
                schema: "decision",
                columns: table => new
                {
                    evaluation_sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    strategy_id = table.Column<string>(type: "text", nullable: false),
                    decision_time = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    action = table.Column<short>(type: "smallint", nullable: false),
                    state_before = table.Column<short>(type: "smallint", nullable: false),
                    state_after = table.Column<short>(type: "smallint", nullable: false),
                    trigger_interval = table.Column<short>(type: "smallint", nullable: false),
                    snapshot_version = table.Column<long>(type: "bigint", nullable: false),
                    raw_confidence = table.Column<decimal>(type: "numeric(18,10)", nullable: true),
                    mtf_alignment = table.Column<decimal>(type: "numeric(18,10)", nullable: true),
                    regime = table.Column<short>(type: "smallint", nullable: true),
                    primary_reason_code = table.Column<string>(type: "text", nullable: false),
                    diagnostics = table.Column<string>(type: "jsonb", nullable: false),
                    persisted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_evaluations", x => x.evaluation_sequence);
                });

            migrationBuilder.CreateTable(
                name: "candidate_keys",
                schema: "decision",
                columns: table => new
                {
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_time = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    current_status = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candidate_keys", x => x.candidate_id);
                });

            migrationBuilder.CreateTable(
                name: "candidate_stage_events",
                schema: "decision",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    stage = table.Column<short>(type: "smallint", nullable: false),
                    outcome = table.Column<short>(type: "smallint", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    risk_multiplier = table.Column<decimal>(type: "numeric(18,10)", nullable: true),
                    confidence_before = table.Column<decimal>(type: "numeric(18,10)", nullable: true),
                    confidence_after = table.Column<decimal>(type: "numeric(18,10)", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candidate_stage_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "decision_keys",
                schema: "decision",
                columns: table => new
                {
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_time = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decision_keys", x => x.decision_id);
                });

            migrationBuilder.CreateTable(
                name: "trade_candidates",
                schema: "decision",
                columns: table => new
                {
                    candidate_sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    setup_id = table.Column<string>(type: "text", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    strategy_id = table.Column<string>(type: "text", nullable: false),
                    direction = table.Column<short>(type: "smallint", nullable: false),
                    decision_time = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    reference_bid = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    reference_ask = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    reference_mid = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    stop_price = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    target_price = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    raw_confidence = table.Column<decimal>(type: "numeric(18,10)", nullable: false),
                    mtf_alignment = table.Column<decimal>(type: "numeric(18,10)", nullable: false),
                    regime = table.Column<short>(type: "smallint", nullable: false),
                    setup_calibration_audit = table.Column<string>(type: "jsonb", nullable: false),
                    meta_label_audit = table.Column<string>(type: "jsonb", nullable: false),
                    trading_condition_audit = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trade_candidates", x => x.candidate_sequence);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_evaluations_decision_id",
                schema: "decision",
                table: "agent_evaluations",
                column: "decision_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_evaluations_decision_time",
                schema: "decision",
                table: "agent_evaluations",
                column: "decision_time")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_agent_evaluations_deployment_id_strategy_id_instrument_id_d",
                schema: "decision",
                table: "agent_evaluations",
                columns: new[] { "deployment_id", "strategy_id", "instrument_id", "decision_time" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_evaluations_primary_reason_code_decision_time",
                schema: "decision",
                table: "agent_evaluations",
                columns: new[] { "primary_reason_code", "decision_time" });

            migrationBuilder.CreateIndex(
                name: "ix_candidate_keys_decision_id",
                schema: "decision",
                table: "candidate_keys",
                column: "decision_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_candidate_stage_events_candidate_id_occurred_at",
                schema: "decision",
                table: "candidate_stage_events",
                columns: new[] { "candidate_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_trade_candidates_candidate_id",
                schema: "decision",
                table: "trade_candidates",
                column: "candidate_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_evaluations",
                schema: "decision");

            migrationBuilder.DropTable(
                name: "candidate_keys",
                schema: "decision");

            migrationBuilder.DropTable(
                name: "candidate_stage_events",
                schema: "decision");

            migrationBuilder.DropTable(
                name: "decision_keys",
                schema: "decision");

            migrationBuilder.DropTable(
                name: "trade_candidates",
                schema: "decision");
        }
    }
}
