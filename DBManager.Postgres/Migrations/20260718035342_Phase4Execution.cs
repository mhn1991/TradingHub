using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DBManager.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Phase4Execution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "operations");

            migrationBuilder.EnsureSchema(
                name: "execution");

            migrationBuilder.CreateTable(
                name: "broker_stream_cursors",
                schema: "operations",
                columns: table => new
                {
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_applied_transaction_id = table.Column<string>(type: "text", nullable: true),
                    last_applied_broker_time = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    connection_generation = table.Column<long>(type: "bigint", nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broker_stream_cursors", x => x.broker_account_id);
                });

            migrationBuilder.CreateTable(
                name: "broker_transaction_keys",
                schema: "execution",
                columns: table => new
                {
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_transaction_id = table.Column<string>(type: "text", nullable: false),
                    broker_time = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broker_transaction_keys", x => new { x.broker_account_id, x.broker_transaction_id });
                });

            migrationBuilder.CreateTable(
                name: "broker_transactions",
                schema: "execution",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_transaction_id = table.Column<string>(type: "text", nullable: false),
                    broker_time = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    transaction_type = table.Column<short>(type: "smallint", nullable: false),
                    client_order_id = table.Column<string>(type: "text", nullable: true),
                    broker_order_id = table.Column<string>(type: "text", nullable: true),
                    broker_trade_id = table.Column<string>(type: "text", nullable: true),
                    instrument_id = table.Column<long>(type: "bigint", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    price = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    reason_code = table.Column<string>(type: "text", nullable: true),
                    raw_payload = table.Column<string>(type: "jsonb", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broker_transactions", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "order_commands",
                schema: "execution",
                columns: table => new
                {
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_type = table.Column<short>(type: "smallint", nullable: false),
                    client_order_id = table.Column<string>(type: "text", nullable: false),
                    requested_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    requested_stop = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    requested_target = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_commands", x => x.command_id);
                    table.ForeignKey(
                        name: "fk_order_commands_candidate_keys_candidate_id",
                        column: x => x.candidate_id,
                        principalSchema: "decision",
                        principalTable: "candidate_keys",
                        principalColumn: "candidate_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_events",
                schema: "execution",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    event_type = table.Column<short>(type: "smallint", nullable: false),
                    from_state = table.Column<short>(type: "smallint", nullable: true),
                    to_state = table.Column<short>(type: "smallint", nullable: true),
                    broker_transaction_id = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    price = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    reason_code = table.Column<string>(type: "text", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "position_events",
                schema: "execution",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    position_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    event_type = table.Column<short>(type: "smallint", nullable: false),
                    quantity_delta = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    price = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    realised_pnl_delta = table.Column<decimal>(type: "numeric(28,8)", nullable: true),
                    reason_code = table.Column<string>(type: "text", nullable: true),
                    broker_transaction_id = table.Column<string>(type: "text", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_position_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "positions",
                schema: "execution",
                columns: table => new
                {
                    position_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    strategy_id = table.Column<string>(type: "text", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    management_artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    broker_trade_id = table.Column<string>(type: "text", nullable: false),
                    direction = table.Column<short>(type: "smallint", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    original_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    remaining_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    average_entry_price = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    initial_stop_price = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    current_stop_price = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    target_price = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    original_risk = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    remaining_risk = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    realised_pnl = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    unrealised_pnl = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    commission = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    financing = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_positions", x => x.position_id);
                    table.ForeignKey(
                        name: "fk_positions_policy_revisions_policy_revision_id",
                        column: x => x.policy_revision_id,
                        principalSchema: "config",
                        principalTable: "policy_revisions",
                        principalColumn: "policy_revision_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_runs",
                schema: "operations",
                columns: table => new
                {
                    reconciliation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<short>(type: "smallint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    broker_snapshot_time = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    entries_paused = table.Column<bool>(type: "boolean", nullable: false),
                    summary = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_runs", x => x.reconciliation_id);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "execution",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    strategy_id = table.Column<string>(type: "text", nullable: false),
                    client_order_id = table.Column<string>(type: "text", nullable: false),
                    broker_order_id = table.Column<string>(type: "text", nullable: true),
                    broker_trade_id = table.Column<string>(type: "text", nullable: true),
                    direction = table.Column<short>(type: "smallint", nullable: false),
                    requested_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    filled_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    average_fill_price = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    initial_stop_price = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    initial_target_price = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    submission_certainty = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    filled_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.order_id);
                    table.ForeignKey(
                        name: "fk_orders_order_commands_command_id",
                        column: x => x.command_id,
                        principalSchema: "execution",
                        principalTable: "order_commands",
                        principalColumn: "command_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_differences",
                schema: "operations",
                columns: table => new
                {
                    difference_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reconciliation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    difference_type = table.Column<short>(type: "smallint", nullable: false),
                    severity = table.Column<short>(type: "smallint", nullable: false),
                    instrument_id = table.Column<long>(type: "bigint", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    position_id = table.Column<Guid>(type: "uuid", nullable: true),
                    local_value = table.Column<string>(type: "jsonb", nullable: false),
                    broker_value = table.Column<string>(type: "jsonb", nullable: false),
                    action = table.Column<short>(type: "smallint", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    resolution_note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_differences", x => x.difference_id);
                    table.ForeignKey(
                        name: "fk_reconciliation_differences_reconciliation_runs_reconciliati",
                        column: x => x.reconciliation_id,
                        principalSchema: "operations",
                        principalTable: "reconciliation_runs",
                        principalColumn: "reconciliation_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fills",
                schema: "execution",
                columns: table => new
                {
                    fill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_transaction_id = table.Column<string>(type: "text", nullable: false),
                    broker_fill_id = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    price = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    commission = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    financing = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    spread_cost = table.Column<decimal>(type: "numeric(28,8)", nullable: true),
                    slippage_cost = table.Column<decimal>(type: "numeric(28,8)", nullable: true),
                    broker_time = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fills", x => x.fill_id);
                    table.ForeignKey(
                        name: "fk_fills_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "execution",
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fills_broker_account_id_broker_transaction_id",
                schema: "execution",
                table: "fills",
                columns: new[] { "broker_account_id", "broker_transaction_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fills_order_id",
                schema: "execution",
                table: "fills",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_commands_candidate_id",
                schema: "execution",
                table: "order_commands",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_commands_client_order_id",
                schema: "execution",
                table: "order_commands",
                column: "client_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_commands_idempotency_key",
                schema: "execution",
                table: "order_commands",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_events_order_id_occurred_at",
                schema: "execution",
                table: "order_events",
                columns: new[] { "order_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_active",
                schema: "execution",
                table: "orders",
                column: "state",
                filter: "state IN (0,1,2,4,5)");

            migrationBuilder.CreateIndex(
                name: "ix_orders_broker_account_id_state",
                schema: "execution",
                table: "orders",
                columns: new[] { "broker_account_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_client_order_id",
                schema: "execution",
                table: "orders",
                column: "client_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_command_id",
                schema: "execution",
                table: "orders",
                column: "command_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_instrument_id_state",
                schema: "execution",
                table: "orders",
                columns: new[] { "instrument_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_strategy_id_state",
                schema: "execution",
                table: "orders",
                columns: new[] { "strategy_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_position_events_position_id_occurred_at",
                schema: "execution",
                table: "position_events",
                columns: new[] { "position_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_positions_broker_account_id_broker_trade_id_strategy_id",
                schema: "execution",
                table: "positions",
                columns: new[] { "broker_account_id", "broker_trade_id", "strategy_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_positions_policy_revision_id",
                schema: "execution",
                table: "positions",
                column: "policy_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_differences_reconciliation_id",
                schema: "operations",
                table: "reconciliation_differences",
                column: "reconciliation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "broker_stream_cursors",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "broker_transaction_keys",
                schema: "execution");

            migrationBuilder.DropTable(
                name: "broker_transactions",
                schema: "execution");

            migrationBuilder.DropTable(
                name: "fills",
                schema: "execution");

            migrationBuilder.DropTable(
                name: "order_events",
                schema: "execution");

            migrationBuilder.DropTable(
                name: "position_events",
                schema: "execution");

            migrationBuilder.DropTable(
                name: "positions",
                schema: "execution");

            migrationBuilder.DropTable(
                name: "reconciliation_differences",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "execution");

            migrationBuilder.DropTable(
                name: "reconciliation_runs",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "order_commands",
                schema: "execution");
        }
    }
}
