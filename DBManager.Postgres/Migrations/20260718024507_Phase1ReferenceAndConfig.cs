using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DBManager.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Phase1ReferenceAndConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reference");

            migrationBuilder.EnsureSchema(
                name: "config");

            migrationBuilder.CreateTable(
                name: "brokers",
                schema: "reference",
                columns: table => new
                {
                    broker_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    code = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brokers", x => x.broker_id);
                });

            migrationBuilder.CreateTable(
                name: "calibration_artifacts",
                schema: "config",
                columns: table => new
                {
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_type = table.Column<short>(type: "smallint", nullable: false),
                    strategy_id = table.Column<string>(type: "text", nullable: false),
                    strategy_version = table.Column<string>(type: "text", nullable: false),
                    feature_schema_hash = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    storage_uri = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    training_from = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    training_to = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    validation_from = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    validation_to = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    test_from = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    test_to = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    sample_count = table.Column<long>(type: "bigint", nullable: false),
                    metrics = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calibration_artifacts", x => x.artifact_id);
                });

            migrationBuilder.CreateTable(
                name: "deployment_activation_events",
                schema: "config",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_activation_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "instruments",
                schema: "reference",
                columns: table => new
                {
                    instrument_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    canonical_key = table.Column<string>(type: "text", nullable: false),
                    asset_class = table.Column<short>(type: "smallint", nullable: false),
                    base_currency = table.Column<string>(type: "char(3)", nullable: true),
                    quote_currency = table.Column<string>(type: "char(3)", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instruments", x => x.instrument_id);
                });

            migrationBuilder.CreateTable(
                name: "policy_profiles",
                schema: "config",
                columns: table => new
                {
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    strategy_id = table.Column<string>(type: "text", nullable: false),
                    strategy_version = table.Column<string>(type: "text", nullable: false),
                    feature_schema_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_profiles", x => x.policy_id);
                });

            migrationBuilder.CreateTable(
                name: "policy_promotion_events",
                schema: "config",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    from_status = table.Column<short>(type: "smallint", nullable: false),
                    to_status = table.Column<short>(type: "smallint", nullable: false),
                    actor_identity = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_promotion_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "broker_accounts",
                schema: "reference",
                columns: table => new
                {
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_id = table.Column<long>(type: "bigint", nullable: false),
                    external_account_key_hash = table.Column<string>(type: "text", nullable: false),
                    masked_account_name = table.Column<string>(type: "text", nullable: false),
                    environment = table.Column<short>(type: "smallint", nullable: false),
                    account_currency = table.Column<string>(type: "char(3)", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broker_accounts", x => x.broker_account_id);
                    table.ForeignKey(
                        name: "fk_broker_accounts_brokers_broker_id",
                        column: x => x.broker_id,
                        principalSchema: "reference",
                        principalTable: "brokers",
                        principalColumn: "broker_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_revisions",
                schema: "config",
                columns: table => new
                {
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    policy_document = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_revisions", x => x.policy_revision_id);
                    table.ForeignKey(
                        name: "fk_policy_revisions_policy_profiles_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "config",
                        principalTable: "policy_profiles",
                        principalColumn: "policy_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "broker_instruments",
                schema: "reference",
                columns: table => new
                {
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    metadata_revision = table.Column<long>(type: "bigint", nullable: false),
                    broker_symbol = table.Column<string>(type: "text", nullable: false),
                    minimum_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    quantity_step = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    maximum_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    price_precision = table.Column<short>(type: "smallint", nullable: false),
                    quantity_precision = table.Column<short>(type: "smallint", nullable: false),
                    pip_location = table.Column<short>(type: "smallint", nullable: true),
                    margin_rate = table.Column<decimal>(type: "numeric(18,10)", nullable: false),
                    tradeable = table.Column<bool>(type: "boolean", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broker_instruments", x => new { x.broker_account_id, x.instrument_id, x.metadata_revision });
                    table.ForeignKey(
                        name: "fk_broker_instruments_broker_accounts_broker_account_id",
                        column: x => x.broker_account_id,
                        principalSchema: "reference",
                        principalTable: "broker_accounts",
                        principalColumn: "broker_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_broker_instruments_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "reference",
                        principalTable: "instruments",
                        principalColumn: "instrument_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployments",
                schema: "config",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_instance_id = table.Column<string>(type: "text", nullable: false),
                    execution_mode = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    stopped_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    started_by = table.Column<string>(type: "text", nullable: false),
                    stop_reason = table.Column<string>(type: "text", nullable: true),
                    deployment_hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployments", x => x.deployment_id);
                    table.ForeignKey(
                        name: "fk_deployments_broker_accounts_broker_account_id",
                        column: x => x.broker_account_id,
                        principalSchema: "reference",
                        principalTable: "broker_accounts",
                        principalColumn: "broker_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployments_policy_revisions_policy_revision_id",
                        column: x => x.policy_revision_id,
                        principalSchema: "config",
                        principalTable: "policy_revisions",
                        principalColumn: "policy_revision_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_artifacts",
                schema: "config",
                columns: table => new
                {
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<short>(type: "smallint", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_artifacts", x => new { x.policy_revision_id, x.role });
                    table.ForeignKey(
                        name: "fk_policy_artifacts_calibration_artifacts_artifact_id",
                        column: x => x.artifact_id,
                        principalSchema: "config",
                        principalTable: "calibration_artifacts",
                        principalColumn: "artifact_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_policy_artifacts_policy_revisions_policy_revision_id",
                        column: x => x.policy_revision_id,
                        principalSchema: "config",
                        principalTable: "policy_revisions",
                        principalColumn: "policy_revision_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployment_assignments",
                schema: "config",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    strategy_id = table.Column<string>(type: "text", nullable: false),
                    agent_mode = table.Column<short>(type: "smallint", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_assignments", x => new { x.deployment_id, x.instrument_id, x.strategy_id });
                    table.ForeignKey(
                        name: "fk_deployment_assignments_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalSchema: "config",
                        principalTable: "deployments",
                        principalColumn: "deployment_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployment_assignments_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "reference",
                        principalTable: "instruments",
                        principalColumn: "instrument_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_broker_accounts_broker_id",
                schema: "reference",
                table: "broker_accounts",
                column: "broker_id");

            migrationBuilder.CreateIndex(
                name: "ix_broker_instruments_instrument_id",
                schema: "reference",
                table: "broker_instruments",
                column: "instrument_id");

            migrationBuilder.CreateIndex(
                name: "ix_brokers_code",
                schema: "reference",
                table: "brokers",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_calibration_artifacts_content_hash",
                schema: "config",
                table: "calibration_artifacts",
                column: "content_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_activation_events_deployment_id_occurred_at",
                schema: "config",
                table: "deployment_activation_events",
                columns: new[] { "deployment_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_assignments_instrument_id",
                schema: "config",
                table: "deployment_assignments",
                column: "instrument_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_one_active_per_account",
                schema: "config",
                table: "deployments",
                column: "broker_account_id",
                unique: true,
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_policy_revision_id",
                schema: "config",
                table: "deployments",
                column: "policy_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_instruments_canonical_key",
                schema: "reference",
                table: "instruments",
                column: "canonical_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_policy_artifacts_artifact_id",
                schema: "config",
                table: "policy_artifacts",
                column: "artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_policy_promotion_events_policy_revision_id_occurred_at",
                schema: "config",
                table: "policy_promotion_events",
                columns: new[] { "policy_revision_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_policy_revisions_configuration_hash",
                schema: "config",
                table: "policy_revisions",
                column: "configuration_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_policy_revisions_policy_id_revision",
                schema: "config",
                table: "policy_revisions",
                columns: new[] { "policy_id", "revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "broker_instruments",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "deployment_activation_events",
                schema: "config");

            migrationBuilder.DropTable(
                name: "deployment_assignments",
                schema: "config");

            migrationBuilder.DropTable(
                name: "policy_artifacts",
                schema: "config");

            migrationBuilder.DropTable(
                name: "policy_promotion_events",
                schema: "config");

            migrationBuilder.DropTable(
                name: "deployments",
                schema: "config");

            migrationBuilder.DropTable(
                name: "instruments",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "calibration_artifacts",
                schema: "config");

            migrationBuilder.DropTable(
                name: "broker_accounts",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "policy_revisions",
                schema: "config");

            migrationBuilder.DropTable(
                name: "brokers",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "policy_profiles",
                schema: "config");
        }
    }
}
