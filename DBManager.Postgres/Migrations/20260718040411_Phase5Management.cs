using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DBManager.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Phase5Management : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "management");

            migrationBuilder.CreateTable(
                name: "account_leases",
                schema: "operations",
                columns: table => new
                {
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_instance_id = table.Column<string>(type: "text", nullable: false),
                    lease_generation = table.Column<long>(type: "bigint", nullable: false),
                    acquired_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    heartbeat_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_leases", x => x.broker_account_id);
                });

            migrationBuilder.CreateTable(
                name: "account_snapshots",
                schema: "operations",
                columns: table => new
                {
                    snapshot_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<short>(type: "smallint", nullable: false),
                    snapshot_time = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    balance = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    equity = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    unrealised_pnl = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    realised_pnl = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    margin_used = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    margin_available = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    margin_closeout_ratio = table.Column<decimal>(type: "numeric(18,10)", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_snapshots", x => x.snapshot_id);
                });

            migrationBuilder.CreateTable(
                name: "checkpoints",
                schema: "operations",
                columns: table => new
                {
                    checkpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checkpoint_type = table.Column<short>(type: "smallint", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checkpoints", x => x.checkpoint_id);
                });

            migrationBuilder.CreateTable(
                name: "management_events",
                schema: "management",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    position_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    evaluation_clock = table.Column<short>(type: "smallint", nullable: false),
                    action_type = table.Column<short>(type: "smallint", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    reference_bid = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    reference_ask = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    current_r = table.Column<decimal>(type: "numeric(18,10)", nullable: false),
                    mfe_r = table.Column<decimal>(type: "numeric(18,10)", nullable: false),
                    mae_r = table.Column<decimal>(type: "numeric(18,10)", nullable: false),
                    previous_stop = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    requested_stop = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    confirmed_stop = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    requested_reduction = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    confirmed_reduction = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    broker_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_management_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "operator_commands",
                schema: "operations",
                columns: table => new
                {
                    operator_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_identity = table.Column<string>(type: "text", nullable: false),
                    command_type = table.Column<short>(type: "smallint", nullable: false),
                    target_id = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operator_commands", x => x.operator_command_id);
                });

            migrationBuilder.CreateTable(
                name: "position_management_state",
                schema: "management",
                columns: table => new
                {
                    position_id = table.Column<Guid>(type: "uuid", nullable: false),
                    management_policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<short>(type: "smallint", nullable: false),
                    mfe_price = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    mae_price = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    mfe_r = table.Column<decimal>(type: "numeric(18,10)", nullable: false),
                    mae_r = table.Column<decimal>(type: "numeric(18,10)", nullable: false),
                    highest_profit_floor = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    last_fast_interval_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_main_interval_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_thesis_interval_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_action_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    state_payload = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_position_management_state", x => x.position_id);
                    table.ForeignKey(
                        name: "fk_position_management_state_policy_revisions_management_polic",
                        column: x => x.management_policy_revision_id,
                        principalSchema: "config",
                        principalTable: "policy_revisions",
                        principalColumn: "policy_revision_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_position_management_state_positions_position_id",
                        column: x => x.position_id,
                        principalSchema: "execution",
                        principalTable: "positions",
                        principalColumn: "position_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "safety_events",
                schema: "operations",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    severity = table.Column<short>(type: "smallint", nullable: false),
                    source = table.Column<short>(type: "smallint", nullable: false),
                    directive = table.Column<short>(type: "smallint", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    instrument_id = table.Column<long>(type: "bigint", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    position_id = table.Column<Guid>(type: "uuid", nullable: true),
                    automatic_action = table.Column<string>(type: "jsonb", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    resolved_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_safety_events", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_snapshots_broker_account_id_snapshot_time",
                schema: "operations",
                table: "account_snapshots",
                columns: new[] { "broker_account_id", "snapshot_time" });

            migrationBuilder.CreateIndex(
                name: "ix_checkpoints_deployment_id_checkpoint_type_sequence",
                schema: "operations",
                table: "checkpoints",
                columns: new[] { "deployment_id", "checkpoint_type", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_management_events_broker_command_id",
                schema: "management",
                table: "management_events",
                column: "broker_command_id",
                unique: true,
                filter: "broker_command_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_management_events_position_id_occurred_at",
                schema: "management",
                table: "management_events",
                columns: new[] { "position_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_operator_commands_idempotency_key",
                schema: "operations",
                table: "operator_commands",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_position_management_state_management_policy_revision_id",
                schema: "management",
                table: "position_management_state",
                column: "management_policy_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_safety_events_deployment_id_occurred_at",
                schema: "operations",
                table: "safety_events",
                columns: new[] { "deployment_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_leases",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "account_snapshots",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "checkpoints",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "management_events",
                schema: "management");

            migrationBuilder.DropTable(
                name: "operator_commands",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "position_management_state",
                schema: "management");

            migrationBuilder.DropTable(
                name: "safety_events",
                schema: "operations");
        }
    }
}
