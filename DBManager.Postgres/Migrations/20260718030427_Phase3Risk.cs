using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DBManager.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Phase3Risk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "risk");

            migrationBuilder.CreateTable(
                name: "portfolio_decisions",
                schema: "risk",
                columns: table => new
                {
                    portfolio_decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_epoch = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    requested_risk = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    approved_risk = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    requested_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    approved_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    estimated_margin = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    approved = table.Column<bool>(type: "boolean", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    portfolio_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_portfolio_decisions", x => x.portfolio_decision_id);
                    table.ForeignKey(
                        name: "fk_portfolio_decisions_candidate_keys_candidate_id",
                        column: x => x.candidate_id,
                        principalSchema: "decision",
                        principalTable: "candidate_keys",
                        principalColumn: "candidate_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "position_sizing_evaluations",
                schema: "risk",
                columns: table => new
                {
                    sizing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_snapshot_version = table.Column<long>(type: "bigint", nullable: false),
                    instrument_metadata_revision = table.Column<long>(type: "bigint", nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    equity = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    base_risk_amount = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    stop_distance = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    expected_spread = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    expected_slippage = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    expected_commission = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    conversion_rate = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    conversion_path = table.Column<string>(type: "text", nullable: false),
                    raw_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    normalized_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    estimated_stop_loss = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    estimated_margin = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    approved = table.Column<bool>(type: "boolean", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    audit = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_position_sizing_evaluations", x => x.sizing_id);
                    table.ForeignKey(
                        name: "fk_position_sizing_evaluations_candidate_keys_candidate_id",
                        column: x => x.candidate_id,
                        principalSchema: "decision",
                        principalTable: "candidate_keys",
                        principalColumn: "candidate_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reservation_events",
                schema: "risk",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    from_state = table.Column<short>(type: "smallint", nullable: false),
                    to_state = table.Column<short>(type: "smallint", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    related_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservation_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "risk",
                columns: table => new
                {
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    portfolio_decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    reserved_risk = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    reserved_margin = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    reserved_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservations", x => x.reservation_id);
                    table.ForeignKey(
                        name: "fk_reservations_candidate_keys_candidate_id",
                        column: x => x.candidate_id,
                        principalSchema: "decision",
                        principalTable: "candidate_keys",
                        principalColumn: "candidate_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservations_portfolio_decisions_portfolio_decision_id",
                        column: x => x.portfolio_decision_id,
                        principalSchema: "risk",
                        principalTable: "portfolio_decisions",
                        principalColumn: "portfolio_decision_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_portfolio_decisions_candidate_id",
                schema: "risk",
                table: "portfolio_decisions",
                column: "candidate_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_position_sizing_evaluations_candidate_id",
                schema: "risk",
                table: "position_sizing_evaluations",
                column: "candidate_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reservation_events_reservation_id_occurred_at",
                schema: "risk",
                table: "reservation_events",
                columns: new[] { "reservation_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_active_by_account",
                schema: "risk",
                table: "reservations",
                column: "broker_account_id",
                filter: "state IN (0,1,2,3)");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_broker_account_id_state_expires_at",
                schema: "risk",
                table: "reservations",
                columns: new[] { "broker_account_id", "state", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_candidate_id",
                schema: "risk",
                table: "reservations",
                column: "candidate_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reservations_portfolio_decision_id",
                schema: "risk",
                table: "reservations",
                column: "portfolio_decision_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "position_sizing_evaluations",
                schema: "risk");

            migrationBuilder.DropTable(
                name: "reservation_events",
                schema: "risk");

            migrationBuilder.DropTable(
                name: "reservations",
                schema: "risk");

            migrationBuilder.DropTable(
                name: "portfolio_decisions",
                schema: "risk");
        }
    }
}
