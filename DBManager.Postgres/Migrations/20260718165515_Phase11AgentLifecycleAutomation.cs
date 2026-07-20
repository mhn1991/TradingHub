using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DBManager.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Phase11AgentLifecycleAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "started_by",
                schema: "config",
                table: "deployments",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "started_at",
                schema: "config",
                table: "deployments",
                type: "timestamptz",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz");

            migrationBuilder.AlterColumn<Guid>(
                name: "policy_revision_id",
                schema: "config",
                table: "deployments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<long>(
                name: "concurrency_token",
                schema: "config",
                table: "deployments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "draining_at",
                schema: "config",
                table: "deployments",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "environment",
                schema: "config",
                table: "deployments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "faulted_at",
                schema: "config",
                table: "deployments",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                schema: "config",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "paused_at",
                schema: "config",
                table: "deployments",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "preparing_at",
                schema: "config",
                table: "deployments",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "requested_at",
                schema: "config",
                table: "deployments",
                type: "timestamptz",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "requested_by",
                schema: "config",
                table: "deployments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "running_at",
                schema: "config",
                table: "deployments",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "warming_up_at",
                schema: "config",
                table: "deployments",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE config.deployments
                SET requested_at = COALESCE(started_at, now()),
                    requested_by = COALESCE(NULLIF(started_by, ''), 'legacy-migration'),
                    environment = 'legacy',
                    running_at = CASE WHEN status = 0 THEN started_at ELSE NULL END,
                    concurrency_token = 1
                WHERE requested_by = '';
                """);

            migrationBuilder.CreateTable(
                name: "deployment_agents",
                schema: "config",
                columns: table => new
                {
                    deployment_agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    strategy_id = table.Column<string>(type: "text", nullable: false),
                    agent_mode = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    package_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    stopped_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    fault_code = table.Column<string>(type: "text", nullable: true),
                    fault_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_agents", x => x.deployment_agent_id);
                    table.ForeignKey(
                        name: "fk_deployment_agents_broker_accounts_broker_account_id",
                        column: x => x.broker_account_id,
                        principalSchema: "reference",
                        principalTable: "broker_accounts",
                        principalColumn: "broker_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployment_agents_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalSchema: "config",
                        principalTable: "deployments",
                        principalColumn: "deployment_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployment_agents_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "reference",
                        principalTable: "instruments",
                        principalColumn: "instrument_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployment_agents_policy_revisions_policy_revision_id",
                        column: x => x.policy_revision_id,
                        principalSchema: "config",
                        principalTable: "policy_revisions",
                        principalColumn: "policy_revision_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "parity_certifications",
                schema: "config",
                columns: table => new
                {
                    certification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    source_commit = table.Column<string>(type: "text", nullable: false),
                    recording_hash = table.Column<string>(type: "text", nullable: false),
                    simulator_build_hash = table.Column<string>(type: "text", nullable: false),
                    live_build_hash = table.Column<string>(type: "text", nullable: false),
                    compared_epoch_count = table.Column<int>(type: "integer", nullable: false),
                    mismatch_count = table.Column<int>(type: "integer", nullable: false),
                    mismatch_details_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    certified_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    certified_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parity_certifications", x => x.certification_id);
                    table.ForeignKey(
                        name: "fk_parity_certifications_policy_revisions_policy_revision_id",
                        column: x => x.policy_revision_id,
                        principalSchema: "config",
                        principalTable: "policy_revisions",
                        principalColumn: "policy_revision_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_permission_events",
                schema: "config",
                columns: table => new
                {
                    permission_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_environment = table.Column<string>(type: "text", nullable: false),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    permissions = table.Column<int>(type: "integer", nullable: false),
                    granted = table.Column<bool>(type: "boolean", nullable: false),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    actor_identity = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_permission_events", x => x.permission_event_id);
                    table.ForeignKey(
                        name: "fk_policy_permission_events_policy_revisions_policy_revision_id",
                        column: x => x.policy_revision_id,
                        principalSchema: "config",
                        principalTable: "policy_revisions",
                        principalColumn: "policy_revision_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployment_commands",
                schema: "operations",
                columns: table => new
                {
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    command_type = table.Column<short>(type: "smallint", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    expected_version = table.Column<long>(type: "bigint", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    claimed_by_host = table.Column<string>(type: "text", nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_commands", x => x.command_id);
                    table.ForeignKey(
                        name: "fk_deployment_commands_deployment_agents_deployment_agent_id",
                        column: x => x.deployment_agent_id,
                        principalSchema: "config",
                        principalTable: "deployment_agents",
                        principalColumn: "deployment_agent_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployment_commands_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalSchema: "config",
                        principalTable: "deployments",
                        principalColumn: "deployment_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployment_events",
                schema: "operations",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    actor_identity = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: true),
                    detail_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_events", x => x.event_id);
                    table.ForeignKey(
                        name: "fk_deployment_events_deployment_agents_deployment_agent_id",
                        column: x => x.deployment_agent_id,
                        principalSchema: "config",
                        principalTable: "deployment_agents",
                        principalColumn: "deployment_agent_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployment_events_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalSchema: "config",
                        principalTable: "deployments",
                        principalColumn: "deployment_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deployments_idempotency_key",
                schema: "config",
                table: "deployments",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_agents_deployment_id_instrument_id_policy_revisi",
                schema: "config",
                table: "deployment_agents",
                columns: new[] { "deployment_id", "instrument_id", "policy_revision_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_agents_instrument_id",
                schema: "config",
                table: "deployment_agents",
                column: "instrument_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_agents_one_executable_owner",
                schema: "config",
                table: "deployment_agents",
                columns: new[] { "broker_account_id", "instrument_id" },
                unique: true,
                filter: "enabled AND agent_mode IN (2, 3) AND status IN (0, 1, 2, 3, 4, 5, 6)");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_agents_policy_revision_id",
                schema: "config",
                table: "deployment_agents",
                column: "policy_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_commands_deployment_agent_id",
                schema: "operations",
                table: "deployment_commands",
                column: "deployment_agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_commands_deployment_id",
                schema: "operations",
                table: "deployment_commands",
                column: "deployment_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_commands_idempotency_key",
                schema: "operations",
                table: "deployment_commands",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_commands_status_requested_at",
                schema: "operations",
                table: "deployment_commands",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_events_deployment_agent_id",
                schema: "operations",
                table: "deployment_events",
                column: "deployment_agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_events_deployment_id_occurred_at",
                schema: "operations",
                table: "deployment_events",
                columns: new[] { "deployment_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_parity_certifications_policy_revision_id_certified_at",
                schema: "config",
                table: "parity_certifications",
                columns: new[] { "policy_revision_id", "certified_at" });

            migrationBuilder.CreateIndex(
                name: "ix_policy_permission_events_policy_revision_id_broker_environm",
                schema: "config",
                table: "policy_permission_events",
                columns: new[] { "policy_revision_id", "broker_environment", "broker_account_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deployment_commands",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "deployment_events",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "parity_certifications",
                schema: "config");

            migrationBuilder.DropTable(
                name: "policy_permission_events",
                schema: "config");

            migrationBuilder.DropTable(
                name: "deployment_agents",
                schema: "config");

            migrationBuilder.DropIndex(
                name: "ix_deployments_idempotency_key",
                schema: "config",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "concurrency_token",
                schema: "config",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "draining_at",
                schema: "config",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "environment",
                schema: "config",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "faulted_at",
                schema: "config",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                schema: "config",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "paused_at",
                schema: "config",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "preparing_at",
                schema: "config",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "requested_at",
                schema: "config",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "requested_by",
                schema: "config",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "running_at",
                schema: "config",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "warming_up_at",
                schema: "config",
                table: "deployments");

            migrationBuilder.AlterColumn<string>(
                name: "started_by",
                schema: "config",
                table: "deployments",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "started_at",
                schema: "config",
                table: "deployments",
                type: "timestamptz",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "policy_revision_id",
                schema: "config",
                table: "deployments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
