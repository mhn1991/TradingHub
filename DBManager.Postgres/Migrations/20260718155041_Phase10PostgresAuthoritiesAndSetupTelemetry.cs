using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DBManager.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Phase10PostgresAuthoritiesAndSetupTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_policy_revisions_configuration_hash",
                schema: "config",
                table: "policy_revisions");

            migrationBuilder.AddColumn<int>(
                name: "revision",
                schema: "simulation",
                table: "experiments",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "archived_at",
                schema: "simulation",
                table: "experiment_profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "content_size_bytes",
                schema: "config",
                table: "calibration_artifacts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "media_type",
                schema: "config",
                table: "calibration_artifacts",
                type: "text",
                nullable: false,
                defaultValue: "application/octet-stream");

            migrationBuilder.AddColumn<string>(
                name: "payload",
                schema: "config",
                table: "calibration_artifacts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "serializer_version",
                schema: "config",
                table: "calibration_artifacts",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "calibration_artifact_status_events",
                schema: "config",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<short>(type: "smallint", nullable: false),
                    to_status = table.Column<short>(type: "smallint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    actor_identity = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calibration_artifact_status_events", x => x.event_id);
                    table.ForeignKey(
                        name: "fk_calibration_artifact_status_events_calibration_artifacts_ar",
                        column: x => x.artifact_id,
                        principalSchema: "config",
                        principalTable: "calibration_artifacts",
                        principalColumn: "artifact_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "calibration_bundle_candidates",
                schema: "config",
                columns: table => new
                {
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    setup_artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meta_model_artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    management_artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_profile = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    reviewed_by = table.Column<string>(type: "text", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    approved_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_profile_revision = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calibration_bundle_candidates", x => x.candidate_id);
                    table.ForeignKey(
                        name: "fk_calibration_bundle_candidates_calibration_artifacts_managem",
                        column: x => x.management_artifact_id,
                        principalSchema: "config",
                        principalTable: "calibration_artifacts",
                        principalColumn: "artifact_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_calibration_bundle_candidates_calibration_artifacts_meta_mo",
                        column: x => x.meta_model_artifact_id,
                        principalSchema: "config",
                        principalTable: "calibration_artifacts",
                        principalColumn: "artifact_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_calibration_bundle_candidates_calibration_artifacts_setup_a",
                        column: x => x.setup_artifact_id,
                        principalSchema: "config",
                        principalTable: "calibration_artifacts",
                        principalColumn: "artifact_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "setup_state_events",
                schema: "decision",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    runtime_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    strategy_id = table.Column<string>(type: "text", nullable: false),
                    playbook_id = table.Column<string>(type: "text", nullable: false),
                    setup_instance_id = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    state_before = table.Column<short>(type: "smallint", nullable: false),
                    state_after = table.Column<short>(type: "smallint", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    snapshot_version = table.Column<long>(type: "bigint", nullable: false),
                    features = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_setup_state_events", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_policy_revisions_configuration_hash",
                schema: "config",
                table: "policy_revisions",
                column: "configuration_hash");

            migrationBuilder.CreateIndex(
                name: "ix_calibration_artifact_status_events_artifact_id_occurred_at",
                schema: "config",
                table: "calibration_artifact_status_events",
                columns: new[] { "artifact_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_calibration_bundle_candidates_management_artifact_id",
                schema: "config",
                table: "calibration_bundle_candidates",
                column: "management_artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_calibration_bundle_candidates_meta_model_artifact_id",
                schema: "config",
                table: "calibration_bundle_candidates",
                column: "meta_model_artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_calibration_bundle_candidates_setup_artifact_id",
                schema: "config",
                table: "calibration_bundle_candidates",
                column: "setup_artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_calibration_bundle_candidates_status_created_at",
                schema: "config",
                table: "calibration_bundle_candidates",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_setup_state_events_instrument_id_strategy_id_occurred_at",
                schema: "decision",
                table: "setup_state_events",
                columns: new[] { "instrument_id", "strategy_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_setup_state_events_occurred_at",
                schema: "decision",
                table: "setup_state_events",
                column: "occurred_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_setup_state_events_runtime_session_id_agent_instance_id_occ",
                schema: "decision",
                table: "setup_state_events",
                columns: new[] { "runtime_session_id", "agent_instance_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calibration_artifact_status_events",
                schema: "config");

            migrationBuilder.DropTable(
                name: "calibration_bundle_candidates",
                schema: "config");

            migrationBuilder.DropTable(
                name: "setup_state_events",
                schema: "decision");

            migrationBuilder.DropIndex(
                name: "ix_policy_revisions_configuration_hash",
                schema: "config",
                table: "policy_revisions");

            migrationBuilder.DropColumn(
                name: "revision",
                schema: "simulation",
                table: "experiments");

            migrationBuilder.DropColumn(
                name: "archived_at",
                schema: "simulation",
                table: "experiment_profiles");

            migrationBuilder.DropColumn(
                name: "content_size_bytes",
                schema: "config",
                table: "calibration_artifacts");

            migrationBuilder.DropColumn(
                name: "media_type",
                schema: "config",
                table: "calibration_artifacts");

            migrationBuilder.DropColumn(
                name: "payload",
                schema: "config",
                table: "calibration_artifacts");

            migrationBuilder.DropColumn(
                name: "serializer_version",
                schema: "config",
                table: "calibration_artifacts");

            migrationBuilder.CreateIndex(
                name: "ix_policy_revisions_configuration_hash",
                schema: "config",
                table: "policy_revisions",
                column: "configuration_hash",
                unique: true);
        }
    }
}
