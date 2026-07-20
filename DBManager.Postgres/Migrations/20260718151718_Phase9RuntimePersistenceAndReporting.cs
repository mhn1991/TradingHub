using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DBManager.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Phase9RuntimePersistenceAndReporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "simulation");

            migrationBuilder.CreateTable(
                name: "agent_activity_windows",
                schema: "operations",
                columns: table => new
                {
                    runtime_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<long>(type: "bigint", nullable: false),
                    strategy_id = table.Column<string>(type: "text", nullable: false),
                    playbook_key = table.Column<string>(type: "text", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evaluations_observed = table.Column<long>(type: "bigint", nullable: false),
                    no_setup_count = table.Column<long>(type: "bigint", nullable: false),
                    warmup_count = table.Column<long>(type: "bigint", nullable: false),
                    buy_count = table.Column<long>(type: "bigint", nullable: false),
                    sell_count = table.Column<long>(type: "bigint", nullable: false),
                    hold_count = table.Column<long>(type: "bigint", nullable: false),
                    candidates_created = table.Column<long>(type: "bigint", nullable: false),
                    candidates_rejected = table.Column<long>(type: "bigint", nullable: false),
                    state_transitions = table.Column<long>(type: "bigint", nullable: false),
                    timeout_count = table.Column<long>(type: "bigint", nullable: false),
                    error_count = table.Column<long>(type: "bigint", nullable: false),
                    reason_counts_json = table.Column<string>(type: "jsonb", nullable: false),
                    mean_evaluation_milliseconds = table.Column<double>(type: "double precision", nullable: false),
                    min_evaluation_milliseconds = table.Column<double>(type: "double precision", nullable: false),
                    max_evaluation_milliseconds = table.Column<double>(type: "double precision", nullable: false),
                    mean_candidate_confidence = table.Column<double>(type: "double precision", nullable: true),
                    latest_snapshot_version = table.Column<long>(type: "bigint", nullable: true),
                    latest_market_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_activity_windows", x => new { x.runtime_session_id, x.agent_instance_id, x.instrument_id, x.strategy_id, x.playbook_key, x.window_start });
                });

            migrationBuilder.CreateTable(
                name: "broker_environments",
                schema: "reference",
                columns: table => new
                {
                    broker_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_id = table.Column<long>(type: "bigint", nullable: false),
                    environment_code = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    is_live = table.Column<bool>(type: "boolean", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broker_environments", x => x.broker_environment_id);
                    table.ForeignKey(
                        name: "fk_broker_environments_brokers_broker_id",
                        column: x => x.broker_id,
                        principalSchema: "reference",
                        principalTable: "brokers",
                        principalColumn: "broker_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "experiment_comparisons",
                schema: "simulation",
                columns: table => new
                {
                    comparison_id = table.Column<Guid>(type: "uuid", nullable: false),
                    experiment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    baseline_simulation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_simulation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metrics_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_experiment_comparisons", x => x.comparison_id);
                });

            migrationBuilder.CreateTable(
                name: "experiment_profiles",
                schema: "simulation",
                columns: table => new
                {
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_experiment_profiles", x => x.profile_id);
                });

            migrationBuilder.CreateTable(
                name: "experiment_runs",
                schema: "simulation",
                columns: table => new
                {
                    experiment_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    experiment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    simulation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<string>(type: "text", nullable: true),
                    shared_analysis_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    analysis_warmup_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    learning_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    learning_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    embargo_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    embargo_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    evaluation_warmup_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    evaluation_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    evaluation_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_experiment_runs", x => x.experiment_run_id);
                });

            migrationBuilder.CreateTable(
                name: "experiments",
                schema: "simulation",
                columns: table => new
                {
                    experiment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    resolved_configuration_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_experiments", x => x.experiment_id);
                });

            migrationBuilder.CreateTable(
                name: "job_events",
                schema: "simulation",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    simulation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_revision = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    details_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "job_progress",
                schema: "simulation",
                columns: table => new
                {
                    simulation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    market_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_candles = table.Column<long>(type: "bigint", nullable: false),
                    progress_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    candles_per_second = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    current_phase = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_progress", x => x.simulation_id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "simulation",
                columns: table => new
                {
                    simulation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_experiment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_kind = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    phase = table.Column<short>(type: "smallint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    resolved_configuration_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_request_id = table.Column<string>(type: "text", nullable: true),
                    input_hash = table.Column<string>(type: "text", nullable: true),
                    output_manifest_id = table.Column<Guid>(type: "uuid", nullable: true),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    failure_detail = table.Column<string>(type: "text", nullable: true),
                    pause_requested = table.Column<bool>(type: "boolean", nullable: false),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false),
                    heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.simulation_id);
                });

            migrationBuilder.CreateTable(
                name: "live_event_journal",
                schema: "operations",
                columns: table => new
                {
                    event_sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream_name = table.Column<string>(type: "text", nullable: false),
                    payload_type = table.Column<string>(type: "text", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    payload_hash = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_live_event_journal", x => x.event_sequence);
                });

            migrationBuilder.CreateTable(
                name: "manual_approval_candidates",
                schema: "operations",
                columns: table => new
                {
                    candidate_id = table.Column<string>(type: "text", nullable: false),
                    candidate_fingerprint = table.Column<string>(type: "text", nullable: false),
                    reservation_id = table.Column<string>(type: "text", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_revision = table.Column<string>(type: "text", nullable: true),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    candidate_json = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_by = table.Column<string>(type: "text", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_reason = table.Column<string>(type: "text", nullable: true),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manual_approval_candidates", x => x.candidate_id);
                });

            migrationBuilder.CreateTable(
                name: "manual_approval_events",
                schema: "operations",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    candidate_id = table.Column<string>(type: "text", nullable: false),
                    from_state = table.Column<short>(type: "smallint", nullable: false),
                    to_state = table.Column<short>(type: "smallint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    candidate_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manual_approval_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "output_blobs",
                schema: "simulation",
                columns: table => new
                {
                    blob_id = table.Column<Guid>(type: "uuid", nullable: false),
                    output_manifest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    simulation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blob_kind = table.Column<short>(type: "smallint", nullable: false),
                    storage_uri = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    media_type = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retention_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_output_blobs", x => x.blob_id);
                });

            migrationBuilder.CreateTable(
                name: "output_manifests",
                schema: "simulation",
                columns: table => new
                {
                    output_manifest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    simulation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_output_manifests", x => x.output_manifest_id);
                });

            migrationBuilder.CreateTable(
                name: "profile_revisions",
                schema: "simulation",
                columns: table => new
                {
                    profile_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    settings_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_profile_revisions", x => x.profile_revision_id);
                });

            migrationBuilder.CreateTable(
                name: "runtime_profiles",
                schema: "config",
                columns: table => new
                {
                    runtime_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_kind = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runtime_profiles", x => x.runtime_profile_id);
                });

            migrationBuilder.CreateTable(
                name: "runtime_sessions",
                schema: "operations",
                columns: table => new
                {
                    runtime_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_kind = table.Column<short>(type: "smallint", nullable: false),
                    simulation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    research_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    host_instance_id = table.Column<string>(type: "text", nullable: false),
                    machine_name = table.Column<string>(type: "text", nullable: false),
                    process_id = table.Column<int>(type: "integer", nullable: false),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    termination_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runtime_sessions", x => x.runtime_session_id);
                });

            migrationBuilder.CreateTable(
                name: "trading_telemetry_events",
                schema: "operations",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    runtime_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    severity = table.Column<short>(type: "smallint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    experiment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    simulation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    research_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    instrument_id = table.Column<long>(type: "bigint", nullable: true),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_id = table.Column<string>(type: "text", nullable: true),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fill_id = table.Column<Guid>(type: "uuid", nullable: true),
                    position_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trading_telemetry_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "broker_account_settings_revisions",
                schema: "reference",
                columns: table => new
                {
                    broker_account_settings_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    account_alias = table.Column<string>(type: "text", nullable: false),
                    external_account_id = table.Column<string>(type: "text", nullable: true),
                    account_currency = table.Column<string>(type: "char(3)", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broker_account_settings_revisions", x => x.broker_account_settings_revision_id);
                    table.ForeignKey(
                        name: "fk_broker_account_settings_revisions_broker_accounts_broker_ac",
                        column: x => x.broker_account_id,
                        principalSchema: "reference",
                        principalTable: "broker_accounts",
                        principalColumn: "broker_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_broker_account_settings_revisions_broker_environments_broke",
                        column: x => x.broker_environment_id,
                        principalSchema: "reference",
                        principalTable: "broker_environments",
                        principalColumn: "broker_environment_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "broker_endpoint_revisions",
                schema: "reference",
                columns: table => new
                {
                    broker_endpoint_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    base_address = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broker_endpoint_revisions", x => x.broker_endpoint_revision_id);
                    table.ForeignKey(
                        name: "fk_broker_endpoint_revisions_broker_environments_broker_enviro",
                        column: x => x.broker_environment_id,
                        principalSchema: "reference",
                        principalTable: "broker_environments",
                        principalColumn: "broker_environment_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "credential_references",
                schema: "reference",
                columns: table => new
                {
                    credential_reference_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    secret_key = table.Column<string>(type: "text", nullable: false),
                    secret_version = table.Column<string>(type: "text", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credential_references", x => x.credential_reference_id);
                    table.ForeignKey(
                        name: "fk_credential_references_broker_environments_broker_environmen",
                        column: x => x.broker_environment_id,
                        principalSchema: "reference",
                        principalTable: "broker_environments",
                        principalColumn: "broker_environment_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "runtime_profile_revisions",
                schema: "config",
                columns: table => new
                {
                    runtime_profile_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    runtime_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    settings_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runtime_profile_revisions", x => x.runtime_profile_revision_id);
                    table.ForeignKey(
                        name: "fk_runtime_profile_revisions_runtime_profiles_runtime_profile_",
                        column: x => x.runtime_profile_id,
                        principalSchema: "config",
                        principalTable: "runtime_profiles",
                        principalColumn: "runtime_profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_activity_windows_runtime_session_id_window_start",
                schema: "operations",
                table: "agent_activity_windows",
                columns: new[] { "runtime_session_id", "window_start" });

            migrationBuilder.CreateIndex(
                name: "ix_broker_account_settings_one_active",
                schema: "reference",
                table: "broker_account_settings_revisions",
                column: "broker_environment_id",
                unique: true,
                filter: "active");

            migrationBuilder.CreateIndex(
                name: "ix_broker_account_settings_revisions_broker_account_id",
                schema: "reference",
                table: "broker_account_settings_revisions",
                column: "broker_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_broker_account_settings_revisions_broker_environment_id_rev",
                schema: "reference",
                table: "broker_account_settings_revisions",
                columns: new[] { "broker_environment_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_broker_endpoint_revisions_broker_environment_id_kind_revisi",
                schema: "reference",
                table: "broker_endpoint_revisions",
                columns: new[] { "broker_environment_id", "kind", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_broker_endpoint_revisions_one_active",
                schema: "reference",
                table: "broker_endpoint_revisions",
                columns: new[] { "broker_environment_id", "kind" },
                unique: true,
                filter: "active");

            migrationBuilder.CreateIndex(
                name: "ix_broker_environments_broker_id_environment_code",
                schema: "reference",
                table: "broker_environments",
                columns: new[] { "broker_id", "environment_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_credential_references_one_active",
                schema: "reference",
                table: "credential_references",
                columns: new[] { "broker_environment_id", "purpose" },
                unique: true,
                filter: "active");

            migrationBuilder.CreateIndex(
                name: "ix_experiment_comparisons_experiment_id",
                schema: "simulation",
                table: "experiment_comparisons",
                column: "experiment_id");

            migrationBuilder.CreateIndex(
                name: "ix_experiment_profiles_name",
                schema: "simulation",
                table: "experiment_profiles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_experiment_runs_experiment_id_simulation_id",
                schema: "simulation",
                table: "experiment_runs",
                columns: new[] { "experiment_id", "simulation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_experiments_status_created_at",
                schema: "simulation",
                table: "experiments",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_job_events_simulation_id_job_revision_event_type",
                schema: "simulation",
                table: "job_events",
                columns: new[] { "simulation_id", "job_revision", "event_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_job_events_simulation_id_occurred_at",
                schema: "simulation",
                table: "job_events",
                columns: new[] { "simulation_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_parent_experiment_id",
                schema: "simulation",
                table: "jobs",
                column: "parent_experiment_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_status_requested_at",
                schema: "simulation",
                table: "jobs",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_live_event_journal_deployment_id_stream_name_occurred_at",
                schema: "operations",
                table: "live_event_journal",
                columns: new[] { "deployment_id", "stream_name", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_live_event_journal_event_id",
                schema: "operations",
                table: "live_event_journal",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_manual_approval_candidates_candidate_fingerprint",
                schema: "operations",
                table: "manual_approval_candidates",
                column: "candidate_fingerprint");

            migrationBuilder.CreateIndex(
                name: "ix_manual_approval_candidates_state_expires_at",
                schema: "operations",
                table: "manual_approval_candidates",
                columns: new[] { "state", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_manual_approval_events_candidate_id_candidate_revision",
                schema: "operations",
                table: "manual_approval_events",
                columns: new[] { "candidate_id", "candidate_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_output_blobs_content_hash",
                schema: "simulation",
                table: "output_blobs",
                column: "content_hash");

            migrationBuilder.CreateIndex(
                name: "ix_output_blobs_simulation_id_blob_kind",
                schema: "simulation",
                table: "output_blobs",
                columns: new[] { "simulation_id", "blob_kind" });

            migrationBuilder.CreateIndex(
                name: "ix_output_manifests_simulation_id_manifest_hash",
                schema: "simulation",
                table: "output_manifests",
                columns: new[] { "simulation_id", "manifest_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_profile_revisions_profile_id_revision",
                schema: "simulation",
                table: "profile_revisions",
                columns: new[] { "profile_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runtime_profile_revisions_runtime_profile_id_revision",
                schema: "config",
                table: "runtime_profile_revisions",
                columns: new[] { "runtime_profile_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runtime_profile_revisions_settings_hash",
                schema: "config",
                table: "runtime_profile_revisions",
                column: "settings_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runtime_profiles_profile_kind_name",
                schema: "config",
                table: "runtime_profiles",
                columns: new[] { "profile_kind", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runtime_sessions_deployment_id",
                schema: "operations",
                table: "runtime_sessions",
                column: "deployment_id");

            migrationBuilder.CreateIndex(
                name: "ix_runtime_sessions_session_kind_started_at",
                schema: "operations",
                table: "runtime_sessions",
                columns: new[] { "session_kind", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_runtime_sessions_simulation_id",
                schema: "operations",
                table: "runtime_sessions",
                column: "simulation_id");

            migrationBuilder.CreateIndex(
                name: "ix_trading_telemetry_events_candidate_id",
                schema: "operations",
                table: "trading_telemetry_events",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_trading_telemetry_events_position_id",
                schema: "operations",
                table: "trading_telemetry_events",
                column: "position_id");

            migrationBuilder.CreateIndex(
                name: "ix_trading_telemetry_events_reason_code_occurred_at",
                schema: "operations",
                table: "trading_telemetry_events",
                columns: new[] { "reason_code", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_trading_telemetry_events_runtime_session_id_occurred_at",
                schema: "operations",
                table: "trading_telemetry_events",
                columns: new[] { "runtime_session_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_activity_windows",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "broker_account_settings_revisions",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "broker_endpoint_revisions",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "credential_references",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "experiment_comparisons",
                schema: "simulation");

            migrationBuilder.DropTable(
                name: "experiment_profiles",
                schema: "simulation");

            migrationBuilder.DropTable(
                name: "experiment_runs",
                schema: "simulation");

            migrationBuilder.DropTable(
                name: "experiments",
                schema: "simulation");

            migrationBuilder.DropTable(
                name: "job_events",
                schema: "simulation");

            migrationBuilder.DropTable(
                name: "job_progress",
                schema: "simulation");

            migrationBuilder.DropTable(
                name: "jobs",
                schema: "simulation");

            migrationBuilder.DropTable(
                name: "live_event_journal",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "manual_approval_candidates",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "manual_approval_events",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "output_blobs",
                schema: "simulation");

            migrationBuilder.DropTable(
                name: "output_manifests",
                schema: "simulation");

            migrationBuilder.DropTable(
                name: "profile_revisions",
                schema: "simulation");

            migrationBuilder.DropTable(
                name: "runtime_profile_revisions",
                schema: "config");

            migrationBuilder.DropTable(
                name: "runtime_sessions",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "trading_telemetry_events",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "broker_environments",
                schema: "reference");

            migrationBuilder.DropTable(
                name: "runtime_profiles",
                schema: "config");
        }
    }
}
