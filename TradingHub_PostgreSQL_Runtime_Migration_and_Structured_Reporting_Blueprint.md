# TradingHub PostgreSQL Runtime Migration and Structured Operational Reporting
## Production Implementation Blueprint for Codex

**Repository snapshot reviewed:** `TradingHub_source(4).zip`, 18 July 2026  
**Related design:** `TradingHub_Structural_Confluence_Agent_and_Simulator_Experiment_Blueprint_v2.md`  
**Target framework:** existing repository target (`net10.0`)  
**Primary database:** PostgreSQL through `DBManager.Abstractions` and `DBManager.Postgres`

> **Do not zip, copy, or repackage the repository.** Work directly in the existing local repository. The required deliverable is commit-quality source, migrations, tests, configuration examples, data-migration tooling, and documentation. No archive is needed.

---

# 1. Objective

Implement this work in two strictly ordered stages:

1. **PostgreSQL runtime migration**
   - make PostgreSQL the source of truth for broker metadata, endpoints, non-secret account configuration, runtime profiles, policy/calibration metadata and payloads, simulator/experiment jobs, live durable state, approvals, deployments, decisions, risk, execution, management, reconciliation, research and reporting data;
   - remove duplicated mutable business configuration from `appsettings.json`, environment parsing, in-memory stores and ad-hoc files where PostgreSQL is the correct durable owner;
   - preserve files only for bootstrap configuration, secrets, very large immutable market/replay blobs and an explicitly documented emergency spool.

2. **Structured operational reporting**
   - record meaningful domain events and aggregates from simulation, research, shadow trading and executable live trading;
   - do **not** write a log row for every candle or every ordinary `Observe/Hold` evaluation;
   - produce explainable reports showing what each agent observed, which setup state changed, why candidates were accepted or rejected, what calibration/ML did, how risk and portfolio gates behaved, what orders and management actions occurred, and what finally happened to the trade or shadow candidate.

Stage 2 must not be treated as ordinary text logging. `ILogger` remains for process diagnostics. The reporting system must be based on typed domain records, stable reason codes, correlation IDs and queryable PostgreSQL tables.

---

# 2. Current repository facts Codex must respect

## 2.1 PostgreSQL schema and stores already exist

The repository already contains:

- `DBManager.Abstractions`
- `DBManager.Postgres`
- `DBManager.Tests`

`DBManager.Postgres/TradingHubDbContext.cs` already exposes tables for:

- brokers, accounts, instruments and broker instruments;
- policy profiles, policy revisions, artifacts, deployments and assignments;
- agent evaluations, candidates and candidate-stage events;
- sizing, portfolio decisions and reservations;
- order commands, orders, order events, transactions, fills, positions and position events;
- broker stream cursors, reconciliation runs/differences, leases, safety events, operator commands, checkpoints and account snapshots;
- position-management state and management events;
- research runs, time-series folds and calibration runs;
- candidate outcomes, execution quality and model-monitoring windows;
- backups.

`DBManager.Postgres/ServiceCollectionExtensions.cs` already registers:

- `IPolicyConfigurationStore`
- `IReferenceDataStore`
- `IOperationalDiagnosticsWriter`
- `IAgentDecisionReadStore`
- `IRiskStore`
- `IExecutionStore`
- `IReconciliationStore`
- `IManagementStore`
- `IAccountLeaseStore`
- `IResearchStore`
- `IAnalyticsStore`
- `IResearchBulkStore`
- `IBackupStore`

Do not replace this foundation with another ORM, another database project or an unstructured generic logging table.

## 2.2 The database subsystem is not currently wired into runtime hosts

The database registration and abstractions are currently referenced almost exclusively by `DBManager.Postgres` and `DBManager.Tests`. Searches for `AddDBManagerPostgres`, `IOperationalDiagnosticsWriter`, `IExecutionStore`, `IRiskStore`, `IManagementStore`, `IResearchStore` and related APIs show no production host integration.

The following applications still construct file or in-memory stores directly:

### `DashboardLive/Program.cs`

- `FileSimulationJobRepository`
- `FileCalibrationArtifactRepository`
- `FileTradingPolicyProfileStore`
- `FileCalibrationBundleApprovalStore`

### `LiveTradingHost/Program.cs`

- `FileCalibrationArtifactRepository`
- `FileTradingPolicyProfileStore`
- `FileCalibrationBundleApprovalStore`
- `FileSimulationJobRepository`
- `FileTradingAccountLease`
- `ManualApprovalStore`
- `FileLiveTradingPersistence`

### `BacktestRunner/Program.cs`

- `FileSimulationJobRepository`
- file-backed calibration artifacts and dashboard exports

The first task is therefore **runtime adoption and schema completion**, not merely creating more database entity classes.

## 2.3 Broker endpoint and account configuration is duplicated

Current examples include:

- hard-coded OANDA endpoints in `Brokers/Oanda/OandaBrokerClient.cs`;
- hard-coded Binance endpoints in `Brokers/Binance/BinanceBrokerClient.cs` and `Brokers/Binance/BinanceEndpoints.cs`;
- hard-coded IG endpoints in `Brokers/Ig/IgBrokerClient.cs`;
- Binance public-data URLs in `DashboardLive/LiveFeedOptions.cs` and `DashboardLive/appsettings.json`;
- OANDA base-address overrides, account IDs and tokens in `DashboardLive/OandaWorkspaceOptions.cs`;
- OANDA base-address overrides, mappings, account IDs and tokens in `LiveTradingHost/Configuration/LiveOandaOptions.cs`;
- environment-variable credential and endpoint resolution in `DashboardLive/SimulationBrokerCatalog.cs`, `Simulator/Services/BacktestApplicationService.cs` and `BacktestRunner`;
- broker mappings in multiple option objects.

The database must own endpoint definitions, environment selection, broker/account/instrument mappings and non-secret client settings.

## 2.4 Secrets must not be stored as ordinary database text

Never persist any of these as plaintext in PostgreSQL, JSON diagnostics, reports or process logs:

- OANDA access tokens;
- Binance API secret;
- IG password/API key/session tokens;
- PostgreSQL passwords;
- live-host control token;
- any future OAuth refresh token or private key.

PostgreSQL should store only a **secret reference**, for example:

```text
provider = Environment
secret_key = TRADINGHUB_OANDA_DEMO_PRIMARY_TOKEN
version = optional
```

At runtime, an `ISecretResolver` resolves the value from environment variables, systemd credentials, a local protected secret file, Vault, AWS Secrets Manager or another provider. The first implementation may support environment variables and a no-op development provider, but the database schema must not force plaintext secret storage.

## 2.5 Existing decision diagnostics are useful but not yet sufficient

`DBManager.Abstractions/Decision/IOperationalDiagnosticsWriter.cs` already supports:

- `AgentEvaluationRecord`
- `TradeCandidateRecord`
- `CandidateStageEventRecord`

`DBManager.Postgres/Decision/OperationalBatchWriter.cs` already provides a bounded asynchronous batch path with no synchronous DB round trip in the producer.

Retain and extend this approach. Do not emit `AgentEvaluationRecord` on every candle when nothing meaningful happened. Introduce explicit emission policy and aggregate windows.

## 2.6 Existing `TradingJournal` is not a durable reporting system

`TradingJournal/TradeJournal.cs` currently contains:

- `TradeJournalEventType`
- `TradeJournalEntry`
- `NullTradeJournal`
- bounded `InMemoryTradeJournal`

It is useful for compatibility and local replay, but it is not durable, relationally correlated or sufficient for cross-run reports. Preserve compatibility where needed, but do not use `Snapshot()` as the main reporting source.

## 2.7 Large market and replay data requires a hybrid storage policy

The user wants as much durable state as possible in PostgreSQL, but raw candles and compressed replay chunks can be very large. The correct design is:

- PostgreSQL is the source of truth for identity, configuration, manifests, hashes, lifecycle, metrics and searchable events;
- raw historical candle cache files, imported source files, replay chunks and potentially large binary model files may remain in filesystem/object storage;
- every external blob must have a DB row containing its content hash, size, media type, storage URI, creation time, retention state and owning run;
- no file may be the only durable index of a run or deployment.

---

# 3. Non-negotiable invariants

1. **No database query per candle**
   - runtime configuration is loaded once into an immutable resolved snapshot;
   - analysis and agent hot paths use in-memory values;
   - writes use bounded asynchronous channels or explicit critical transactions.

2. **PostgreSQL becomes the durable source of truth**
   - files may be cache/blob/spool, not the authoritative job/deployment/configuration database;
   - after cutover, restarting a host must reconstruct durable state from PostgreSQL.

3. **Secrets never enter general configuration JSON or telemetry**
   - redact at the boundary;
   - test serialized records and logs for known secret values.

4. **Live safety is fail-closed**
   - failure to persist critical order/reservation/reconciliation state prevents new broker writes;
   - uncertain submission remains a safety pause;
   - database unavailability must never cause the system to guess.

5. **No per-candle operational logs**
   - ordinary observation is counted in aggregate windows;
   - detailed events are emitted only for state transitions, candidates, stage decisions, trades, errors, safety changes and lifecycle events.

6. **Stable correlation IDs**
   - every reportable event must be traceable through experiment/simulation/deployment, agent, decision, candidate, reservation, order, fill and position.

7. **Append-only audit for important transitions**
   - approval, promotion, deployment activation, candidate stages, order state, position state, safety state and operator commands are append-only;
   - current-state tables may be updated transactionally but cannot replace history.

8. **Deterministic simulation reports**
   - the same data/configuration/seed must produce the same domain records and summaries regardless of worker parallelism.

9. **Bounded memory and backpressure**
   - no unbounded queue or in-memory event list;
   - critical events are never silently dropped;
   - low-value counters are aggregated before persistence.

10. **Migration without data loss**
    - use an explicit dual-write/read-compare stage before deleting file-backed authority;
    - provide idempotent import tooling for existing file repositories.

---

# 4. Target architecture

```text
Bootstrap environment
    ├── PostgreSQL connection strings
    ├── secret-provider bootstrap
    └── host bind/bootstrap safety settings
              ↓
PostgreSQL configuration/reference store
    ├── brokers / environments / endpoint revisions
    ├── broker accounts / credential references
    ├── instrument metadata / mappings
    ├── runtime profiles / policy revisions
    ├── simulator experiment profiles
    └── active deployments
              ↓
Resolved immutable runtime snapshot
    ├── validated and hashed
    ├── secret values resolved in memory only
    └── pinned to simulation/deployment revision
              ↓
Simulator / Research / Live runtime
    ├── domain decisions and lifecycle events
    ├── critical execution transactions
    ├── aggregate observation windows
    └── external replay/candle blobs with DB manifests
              ↓
PostgreSQL operational + analytics schemas
              ↓
Report builders / Dashboard API / CLI reports
```

---

# 5. Stage A — PostgreSQL runtime migration

# 5.1 Define the bootstrap boundary

PostgreSQL cannot store the information required to connect to itself. Keep only these values outside the database:

- PostgreSQL connection strings or connection-string secret references;
- secret-provider bootstrap settings;
- ASP.NET/Kestrel bind URL required before application services are constructed;
- an emergency `PersistenceMode`/safe-start switch;
- development-only feature flags needed to bootstrap migrations.

Move business/runtime settings out of `appsettings.json` after database equivalents are active.

Create a shared bootstrap model, preferably under `DBManager.Postgres` or a small host-neutral configuration project:

```csharp
public sealed record PersistenceBootstrapOptions
{
    public const string SectionName = "Persistence";
    public required string MigratorConnectionString { get; init; }
    public required string CriticalConnectionString { get; init; }
    public required string ReadConnectionString { get; init; }
    public required string ResearchConnectionString { get; init; }
    public required string LeaseConnectionString { get; init; }
    public PersistenceRuntimeMode RuntimeMode { get; init; }
}

public enum PersistenceRuntimeMode
{
    FileOnly,      // temporary migration compatibility only
    DualWrite,     // DB + legacy file, compare reads
    PostgresPrimary,
    PostgresOnly
}
```

`PostgresOnly` is the final production mode. File modes must be marked obsolete after migration validation.

# 5.2 Wire `AddDBManagerPostgres` into every executable host

Add project references and registration to:

- `DashboardLive/DashboardLive.csproj` and `DashboardLive/Program.cs`;
- `LiveTradingHost/LiveTradingHost.csproj` and `LiveTradingHost/Program.cs`;
- `BacktestRunner/BacktestRunner.csproj` and its host/service construction;
- `QuantResearchRunner/QuantResearchRunner.csproj` and service construction;
- any dedicated migration/admin executable introduced below.

Do not duplicate option parsing. Add one helper:

```csharp
services.AddTradingHubPersistence(builder.Configuration);
```

It should:

1. bind bootstrap options;
2. register `AddDBManagerPostgres`;
3. register persistence-mode adapters;
4. run health/schema compatibility validation;
5. expose a startup readiness result.

The application must not automatically apply destructive migrations in live mode. Provide an explicit migration command/tool.

# 5.3 Add a database administration CLI

Create a focused executable, for example:

```text
TradingHub.DatabaseAdmin
```

Commands:

```text
migrate
health
schema-version
seed-reference
import-files
verify-parity
backup
restore-test
```

The tool should use existing `ISchemaVersionReader`, `IPersistenceHealthCheck`, `BackupRunner` and stores. Do not hide migration actions inside Dashboard startup.

# 5.4 Complete broker reference schema

The current `reference.brokers`, `reference.broker_accounts`, `reference.instruments` and `reference.broker_instruments` are a good foundation but not sufficient to move endpoints and mappings from code/options.

Add migrations/entities/contracts for the following.

## `reference.broker_environments`

Suggested columns:

```text
broker_environment_id uuid PK
broker_id bigint FK
code text                     -- Demo, Live, Testnet, PublicData
execution_permitted boolean
is_sandbox boolean
created_at timestamptz
retired_at timestamptz null
unique (broker_id, code)
```

## `reference.broker_endpoint_revisions`

Store versioned endpoint configuration:

```text
endpoint_revision_id uuid PK
broker_environment_id uuid FK
endpoint_kind smallint        -- Rest, Streaming, WebSocket, PublicMarketData, Lightstreamer
base_uri text
api_version text null
request_timeout_ms integer
enabled boolean
valid_from timestamptz
valid_to timestamptz null
configuration_hash text
created_by text
created_at timestamptz
```

Constraints:

- URI must be absolute;
- live REST endpoints require HTTPS;
- stream endpoints require HTTPS/WSS as appropriate;
- only one active revision for `(broker_environment_id, endpoint_kind)`;
- code resolves endpoints from DB first;
- hard-coded endpoints remain only as seed constants used by `seed-reference`, not as runtime fallback after `PostgresOnly`.

Move values currently embedded in:

- `Brokers/Oanda/OandaBrokerClient.cs`
- `Brokers/Binance/BinanceBrokerClient.cs`
- `Brokers/Binance/BinanceEndpoints.cs`
- `Brokers/Ig/IgBrokerClient.cs`
- `DashboardLive/LiveFeedOptions.cs`
- `DashboardLive/appsettings.json`

## `reference.credential_references`

```text
credential_reference_id uuid PK
broker_account_id uuid FK null
broker_environment_id uuid FK
credential_kind smallint
provider text                  -- Environment, SystemdCredential, Vault, AwsSecretsManager
secret_key text
secret_version text null
enabled boolean
created_at timestamptz
retired_at timestamptz null
```

Never add a `secret_value` column.

## Broker-account settings

Extend `reference.broker_accounts` or add a versioned account-settings table for:

- requested account/environment;
- account currency;
- request-timeout override;
- trading enabled;
- market-data enabled;
- maximum instrument-metadata age;
- human-readable alias;
- credential reference IDs;
- account identity hash and masked account label.

Do not store raw account IDs unless required by broker APIs and explicitly encrypted. Prefer a secret/provider reference for sensitive identifiers and keep the existing hash/masked representation for reporting. If OANDA account ID is judged operational rather than secret, document that decision and still exclude it from general reports.

## Instrument mapping revisions

`BrokerInstrumentEntity` already stores broker symbols and versioned trading metadata. Extend `IReferenceDataStore` with query and upsert methods; the current interface only registers brokers/accounts/instruments and its comment incorrectly says broker metadata is out of scope.

Add:

```csharp
Task<BrokerEnvironmentDefinition?> GetBrokerEnvironmentAsync(...)
Task<IReadOnlyList<BrokerEndpointDefinition>> GetActiveEndpointsAsync(...)
Task<BrokerAccountDefinition?> GetBrokerAccountAsync(...)
Task<IReadOnlyList<BrokerInstrumentDefinition>> GetTradeableInstrumentsAsync(...)
Task<DurableResult> UpsertBrokerInstrumentRevisionAsync(...)
```

Replace `InstrumentMappings` dictionaries in live/dashboard broker option objects with a resolved `IBrokerInstrumentCatalog` backed by DB snapshots.

# 5.5 Add secret resolution

Create a host-neutral abstraction:

```csharp
public interface ISecretResolver
{
    ValueTask<SecretValue> ResolveAsync(
        CredentialReference reference,
        CancellationToken cancellationToken);
}
```

Initial implementations:

- `EnvironmentSecretResolver`;
- optional `CompositeSecretResolver` selected by `provider`;
- test fake.

`SecretValue` must avoid accidental `ToString()` disclosure and should clear buffers when practical.

Update OANDA/Binance/IG construction so:

1. DB provides broker environment, endpoints, account settings and credential references;
2. secret resolver provides credentials in memory;
3. `OandaOptions`, `BinanceOptions` and `IgOptions` become resolved runtime carriers, not config source-of-truth objects.

# 5.6 Move host runtime profiles to PostgreSQL

Do not place unrelated host and strategy settings in one free-form global table. Introduce immutable revisioned runtime profiles.

Suggested tables:

## `config.runtime_profiles`

```text
runtime_profile_id uuid PK
profile_kind smallint        -- Dashboard, Simulator, LiveHost, Research, BrokerClient
name text
created_at timestamptz
created_by text
```

## `config.runtime_profile_revisions`

```text
runtime_profile_revision_id uuid PK
runtime_profile_id uuid FK
revision integer
status smallint             -- Draft, Approved, Retired
settings jsonb
settings_hash text unique
created_at timestamptz
created_by text
approved_at timestamptz null
approved_by text null
```

Use typed serialization and schema validation, not arbitrary runtime dictionary access.

Move settings currently under:

- `LiveExecution`
- `LiveAccount`
- `LiveDecisionEpoch`
- `LivePolicyBundles`
- `CalibrationRetraining`
- `LiveCalibration`
- simulator experiment orchestration defaults;
- broker client timeouts/mappings;
- Dashboard market-data profiles.

Keep `LiveHost:Urls` bootstrap-only because Kestrel requires it before DB service resolution. `LiveHost:ControlToken` must become a secret reference, not profile JSON.

# 5.7 Resolve and pin immutable configuration snapshots

Create:

```csharp
public interface IRuntimeConfigurationResolver
{
    Task<ResolvedRuntimeConfiguration> ResolveAsync(
        RuntimeConfigurationRequest request,
        CancellationToken cancellationToken);
}
```

The resolved snapshot must include:

- IDs and revisions for all runtime, broker, policy and artifact records;
- endpoint revisions;
- broker-account/instrument metadata revision;
- strategy profile and feature-schema hash;
- calibration artifact IDs/content hashes;
- safety/risk/portfolio/trade-management settings;
- a canonical aggregate `ConfigurationHash`;
- no serialized secret values.

At simulation/experiment/deployment start:

1. resolve from DB;
2. validate cross-record compatibility;
3. compute canonical hash;
4. persist the snapshot identity on the job/deployment;
5. load into memory;
6. never requery mutable configuration in the candle loop.

Live hot-swap must occur only through explicit approved deployment/policy activation, between decision epochs, using existing entry-pinned management rules.

# 5.8 Replace file-backed policy/calibration repositories

Implement PostgreSQL versions of:

- `ICalibrationArtifactRepository`
- `ITradingPolicyProfileStore`
- `ICalibrationBundleApprovalStore`

Use existing tables:

- `config.policy_profiles`
- `config.policy_revisions`
- `config.policy_promotion_events`
- `config.calibration_artifacts`
- `config.policy_artifacts`

The existing `CalibrationArtifactEntity` stores `StorageUri` and metadata but not necessarily payload. The current calibration artifacts are JSON and normally small enough for PostgreSQL. Add one of:

```text
payload jsonb null
binary_payload bytea null
```

with a size policy:

- small deterministic JSON artifact: store in `jsonb`;
- larger binary model: store in external blob store and retain `storage_uri`, hash and size;
- exactly one payload mode is active.

Add artifact content-size, media-type and serializer-version columns.

Preserve:

- immutable content hashes;
- feature-schema compatibility;
- explicit approval;
- no overwrite of approved artifacts;
- status transition history.

# 5.9 Replace `FileSimulationJobRepository`

Create `PostgresSimulationJobRepository` implementing `ISimulationJobRepository`.

Add schema under `research` or a dedicated `simulation` schema. Prefer a dedicated `simulation` schema because simulator jobs are operational runs, while `research.research_runs` remains the statistical experiment record.

Suggested tables:

## `simulation.jobs`

```text
simulation_id uuid PK
parent_experiment_id uuid null
job_kind smallint            -- SingleRun, ExperimentChild, CalibrationTraining
status smallint
phase smallint
revision bigint
requested_at timestamptz
started_at timestamptz null
completed_at timestamptz null
configuration_hash text
resolved_configuration jsonb
input_request_id text
input_hash text null
output_manifest_id uuid null
failure_code text null
failure_detail text null
pause_requested boolean
cancel_requested boolean
heartbeat_at timestamptz null
```

## `simulation.job_progress`

Store latest state, not one row per candle:

```text
simulation_id uuid PK/FK
market_time timestamptz null
processed_candles bigint
progress_percent numeric
candles_per_second numeric
current_phase text
updated_at timestamptz
```

## `simulation.job_events`

Append-only lifecycle events only:

- queued;
- started;
- training started/completed;
- embargo entered/completed;
- evaluation started;
- warm-up completed;
- paused/resumed/cancelled;
- completed/failed;
- recovery/requeue.

Do not store each progress callback as an event. Update `job_progress` using a throttled cadence such as once per second or meaningful percentage/phase change.

## `simulation.output_manifests` and `simulation.output_blobs`

Store replay/chunk metadata:

```text
blob_id uuid
simulation_id uuid
blob_kind smallint
storage_uri text
content_hash text
size_bytes bigint
media_type text
created_at timestamptz
retention_until timestamptz null
```

`ChunkedReplayWriter` may continue writing compressed chunks, but its manifest must be registered transactionally in PostgreSQL.

# 5.10 Persist simulator experiment/profile orchestration

Implement the profile-experiment design from the previous blueprint in PostgreSQL rather than files.

Suggested tables:

- `simulation.experiments`
- `simulation.experiment_profiles`
- `simulation.profile_revisions`
- `simulation.experiment_runs`
- `simulation.experiment_comparisons`

The experiment record must persist exact UTC half-open ranges for:

- training-analysis warm-up;
- learning;
- embargo;
- evaluation warm-up;
- held-out evaluation.

Store profile-to-artifact relationships and resolved hashes. Parallel child runs must be recoverable after host restart.

# 5.11 Wire existing research stores

`DBManager.Postgres/Research/ResearchStore.cs` and entities already support:

- research runs;
- folds;
- purge/embargo;
- calibration runs.

Wire `QuantResearch.Training`, `QuantResearchRunner`, pre-run calibration and live retraining to `IResearchStore`.

Every training operation must create:

1. `ResearchRunEntity`;
2. one or more `TimeSeriesFoldEntity` rows;
3. `CalibrationRunEntity` rows per stage;
4. output artifact rows;
5. summary metrics/failure reason.

The evaluation window must never update the artifact used by that same run.

# 5.12 Replace `FileLiveTradingPersistence`

Do not attempt to map the current file event envelope into one JSON table and stop. Use the specialized existing stores transactionally.

Create an adapter such as:

```csharp
public sealed class PostgresLiveTradingPersistence : ILiveTradingPersistence
```

It should coordinate:

- `IExecutionStore`
- `IRiskStore`
- `IManagementStore`
- `IReconciliationStore`
- checkpoint/account-snapshot operations
- typed operational event writer

Map live events to existing tables:

- order intent/submission/ack/fill → execution schema;
- reservations and portfolio decisions → risk schema;
- management state/actions → management schema;
- stream cursors/reconciliation/checkpoints/account snapshots → operations schema;
- safety/operator actions → operations schema;
- candidate/evaluation/stages → decision schema.

Critical state changes must use explicit transactions and idempotency keys. The database write must be completed before the system claims durable success.

Keep a small emergency spool only for non-order operational telemetry. For order certainty, reservation, reconciliation and checkpoint state, fail closed if durability cannot be established.

# 5.13 Replace file lease with PostgreSQL advisory lease

Use existing `IAccountLeaseStore`/`AccountLeaseStore`, which is designed around a dedicated PostgreSQL session-level advisory lock.

Replace registration:

```csharp
builder.Services.AddSingleton<ITradingAccountLease, FileTradingAccountLease>();
```

with an adapter using `IAccountLeaseStore`.

Retain file lease only in temporary `FileOnly` compatibility mode. In final mode, one broker account must be protected across machines, which a local file lock cannot guarantee.

# 5.14 Persist manual approvals

`LiveTrading/ManualApproval/ManualApprovalStore.cs` is in memory and restored indirectly through live persistence. Create a PostgreSQL-backed approval store or add approval tables/events.

Suggested tables:

```text
operations.manual_approval_candidates
operations.manual_approval_events
```

Store:

- candidate ID/fingerprint;
- reservation ID;
- deployment/policy/config hash;
- proposed quantity/geometry snapshot;
- state;
- expiry;
- reviewer/time/reason;
- exactly-once consumed marker.

Approval and consumption must use optimistic concurrency or row locking. A stale fingerprint must remain rejected.

# 5.15 Preserve large files only as managed blobs

The following may remain file/blob based:

- historical candle cache;
- imported source candle files;
- replay market chunks;
- detailed execution replay chunks;
- large trained binary models;
- DB backups.

But implement `IBlobManifestStore` and register all blobs in PostgreSQL. Add orphan detection and retention cleanup. Paths must never be trusted directly from the client; use server-owned IDs and canonical roots.

# 5.16 Reduce `appsettings.json`

After migration, `LiveTradingHost/appsettings.json` and `DashboardLive/appsettings.json` should contain only safe bootstrap defaults/examples.

Remove or deprecate business configuration sections after DB cutover. Do not silently merge DB and file values. The resolved source must be explicit and included in the configuration snapshot.

# 5.17 Idempotent import and parity tool

Implement `import-files` for existing data:

- file simulation-job snapshots;
- calibration artifacts;
- policy profiles;
- calibration bundle candidates;
- live persistence events/checkpoints;
- appsettings broker endpoints/profiles;
- instrument mappings.

Rules:

- never import tokens/secrets as payload;
- compute content hashes;
- use upsert/idempotency keys;
- produce an import report;
- quarantine malformed records;
- support dry-run;
- verify counts and hashes.

`verify-parity` must compare file and DB projections during `DualWrite` and report discrepancies before cutover.

---

# 6. Stage B — Structured operational event and reporting system

# 6.1 Separate process logging from trading telemetry

Use two systems:

## `ILogger`

For process and infrastructure diagnostics:

- network exception;
- DB timeout;
- service startup/shutdown;
- unexpected exception and stack trace;
- queue health;
- retry warning.

## Typed trading telemetry

For explainable domain history:

- agent setup lifecycle;
- candidate funnel;
- ML/calibration decision;
- risk/portfolio admission;
- order/fill/position lifecycle;
- trade management;
- safety/reconciliation;
- simulator/research/live session lifecycle;
- outcomes and aggregate activity.

Do not create reports by parsing text log messages.

# 6.2 Add a small environment-neutral observability contract

Create a new project if necessary:

```text
TradingObservability.Abstractions
```

It must not reference PostgreSQL, EF Core, ASP.NET, Simulator or LiveTrading. Keep contracts primitive and serializable.

Suggested API:

```csharp
public interface ITradingTelemetryWriter
{
    ValueTask WriteAsync(TradingTelemetryEvent telemetryEvent, CancellationToken cancellationToken);
    ValueTask FlushAsync(CancellationToken cancellationToken);
}

public sealed record TradingTelemetryEvent
{
    public required Guid EventId { get; init; }
    public required TradingTelemetryType Type { get; init; }
    public required TradingTelemetrySeverity Severity { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required string ReasonCode { get; init; }
    public required TelemetryCorrelation Correlation { get; init; }
    public required string PayloadJson { get; init; }
}
```

However, do not use this generic envelope where an existing normalized table already exists. The envelope is for cross-cutting lifecycle/incidents; candidates, orders, fills, management and outcomes continue using specialized stores.

`TradingJournal` should become either:

- a compatibility adapter that writes selected telemetry events; or
- an in-memory secondary sink for local UI/replay.

Do not let old `TradeJournalEntry.Message` become the canonical domain record.

# 6.3 Add runtime-session identity

Create `operations.runtime_sessions`:

```text
runtime_session_id uuid PK
session_kind smallint       -- Simulation, Experiment, Research, LiveShadow, LiveExecutable
simulation_id uuid null
deployment_id uuid null
research_run_id uuid null
host_instance_id text
machine_name text
process_id integer
configuration_hash text
started_at timestamptz
ended_at timestamptz null
status smallint
termination_reason text null
```

Every operational record must link to a runtime session directly or through its deployment/simulation/research run.

# 6.4 Stable correlation model

Use typed identifiers wherever possible:

```text
RuntimeSessionId
ExperimentId
SimulationId
ResearchRunId
DeploymentId
AgentInstanceId
PolicyRevisionId
InstrumentId
DecisionId
CandidateId
ReservationId
OrderCommandId
OrderId
FillId
PositionId
ManagementEventId
```

Add `AgentInstanceId` to decision/candidate reporting. Strategy ID alone is insufficient when multiple profiles or shadow variants run in parallel.

For simulator profiles, include:

- profile ID/revision;
- variant ID;
- shared analysis group ID.

# 6.5 Explicit telemetry emission policy — no per-candle rows

Persist a detailed agent event only when at least one of these is true:

1. agent/playbook state changed;
2. a setup became armed, touched, swept, reclaimed, triggered, invalidated or expired;
3. a candidate was created;
4. a candidate stage accepted/rejected/deferred it;
5. confidence or risk changed due to a named material discipline/ML policy;
6. a trade/order/position/management action occurred;
7. data quality or market availability changed state;
8. safety, reconciliation or health state changed;
9. an error/timeout occurred;
10. a run/deployment/training phase changed.

Do **not** persist a detailed row for:

- each completed candle;
- each indicator recalculation;
- each repeated `Observe/Hold` with unchanged state;
- each unchanged warm-up frame;
- each progress percentage tick.

# 6.6 Aggregate ordinary observations

Add `operations.agent_activity_windows` or `analytics.agent_activity_windows`.

Suggested key:

```text
runtime_session_id
agent_instance_id
instrument_id
strategy_id
playbook_id null
window_start
window_end
```

Counters:

- evaluations observed;
- no-setup count;
- warm-up count;
- buy/sell/hold counts;
- candidates created;
- candidates rejected by stage;
- state-transition counts;
- timeout/error counts;
- reason-code frequency JSON;
- mean/min/max evaluation duration;
- mean confidence for candidates;
- latest snapshot version/market time.

Flush:

- every bounded interval, such as 1–5 minutes live;
- on simulator phase changes;
- at run completion/shutdown;
- when the local counter reaches a threshold.

Use UPSERT by window key. This provides useful reports without millions of no-action rows.

# 6.7 Agent setup lifecycle events

For the Structural Confluence Agent, persist playbook transitions, not candle observations.

Examples:

```text
liquidity_sweep.armed
liquidity_sweep.swept
liquidity_sweep.reclaimed
liquidity_sweep.trigger_confirmed
liquidity_sweep.invalidated
liquidity_sweep.expired
supply_demand.zone_approached
supply_demand.zone_touched
supply_demand.rejection_confirmed
break_retest.accepted_break
break_retest.retest_started
break_retest.retest_held
```

Create `decision.agent_state_events` or a normalized `decision.setup_state_events` table:

```text
event_id bigint identity
runtime_session_id uuid
agent_instance_id uuid
instrument_id bigint
strategy_id text
playbook_id text
setup_instance_id text
occurred_at timestamptz
state_before smallint
state_after smallint
reason_code text
snapshot_version bigint
features jsonb
```

`features` should be a bounded explanation snapshot, not the full annotation frame. Include only relevant values such as zone ID, liquidity pool ID, sweep depth, CCI state, trigger type and distances in ATR.

# 6.8 Candidate funnel

Reuse and wire:

- `decision.agent_evaluations`
- `decision.trade_candidates`
- `decision.candidate_stage_events`

Required stages should cover:

```text
AgentSetup
Geometry
DataQuality
TradingConditions
SetupCalibration
MetaLabel
Sizing
PortfolioAdmission
ManualApproval
Execution
```

Every stage event requires:

- stable reason code;
- outcome;
- confidence before/after where relevant;
- risk multiplier where relevant;
- concise structured details;
- no secrets or giant snapshot.

Do not overwrite a candidate with one final rejection reason. The append-only stage funnel is the report source.

# 6.9 ML/calibration audit

For each candidate, persist:

- artifact ID/content hash;
- feature-schema hash;
- model/calibrator version;
- cohort/bucket key or model output;
- sample count;
- historical win rate/expected R/Brier where applicable;
- input feature summary;
- decision: Neutral/Accept/Reject/Reduce;
- risk multiplier;
- reason code.

The report must make it clear whether:

- deterministic setup rejected the opportunity;
- ML rejected it;
- ML only reduced risk;
- insufficient samples caused neutral behaviour.

Never persist a raw secret or an unbounded full model payload inside candidate diagnostics.

# 6.10 Risk and portfolio reporting

Wire existing `IRiskStore` records for:

- sizing evaluation;
- equity/risk multiplier chain;
- portfolio decision;
- reservation lifecycle.

Reports must show the multiplier chain in order, for example:

```text
Base risk                         1.00
Trading conditions               0.80
Setup calibration                1.00
Meta-label                       0.65
Equity protection                0.75
Final allowed risk               0.39 of base
```

Also report rejection constraints:

- maximum open positions;
- margin usage;
- instrument/currency exposure;
- correlation cluster;
- reservation conflict;
- safety pause.

# 6.11 Execution and position reporting

Wire existing `IExecutionStore` and analytics execution-quality records.

Report:

- candidate reference bid/ask;
- requested quantity;
- client-order ID and idempotency key;
- submission certainty;
- broker order/trade IDs in masked/operator-safe form;
- expected versus actual fill;
- spread/slippage/latency;
- stop/target protection status;
- partial fills;
- rejection/cancellation reason;
- position open/close state.

For simulator and shadow paths, record virtual execution with an explicit execution mode. Never mix paper fills with broker fills without a mode column.

# 6.12 Management reporting

Wire `IManagementStore` and existing management events.

Persist only meaningful actions or state changes:

- break-even armed/applied;
- stop tightened;
- scale-out recommended/executed;
- profit floor advanced;
- MFE giveback reduction;
- stagnation/regime/structure reduction;
- close request and outcome;
- unsupported broker amendment;
- failed amendment/safety response.

Do not log every management evaluation if it returned no change. Aggregate unchanged evaluations in activity windows.

# 6.13 Data-quality and market-health events

Add typed state-change records for:

- gap detected/resolved;
- stale quote entered/resolved;
- market unavailable/available;
- spread stress entered/resolved;
- annotation degraded/recovered;
- agent timed out/recovered;
- DB persistence degraded/recovered.

Repeated identical warnings should be coalesced with count and first/last timestamps.

# 6.14 Simulation and experiment reports

Create a report builder/query service, for example:

```text
TradingReports
```

or under `DashboardContracts` + DB query implementation.

A simulation report must contain:

## Identity and reproducibility

- simulation/experiment ID;
- profile and variant IDs;
- configuration hash;
- policy revision;
- data source/input hash;
- code/build version where available;
- random seed;
- sequential/parallel worker settings.

## Timeline

- analysis warm-up;
- ML learning period;
- purge/embargo;
- evaluation warm-up;
- held-out evaluation;
- phase durations.

## ML/training

- artifacts produced/used;
- folds and sample counts;
- metrics;
- failures/warnings;
- proof artifact was frozen before evaluation.

## Agent work

- observations aggregated;
- setup-state transition counts;
- candidate count by playbook/direction/regime/session;
- top rejection reason codes;
- CCI/S&D/liquidity confirmation breakdown;
- candidate funnel conversion.

## Trading

- trades and net result;
- expectancy, profit factor, drawdown, win/loss ratio;
- MFE/MAE and captured MFE;
- costs, spread and slippage;
- management actions;
- performance by playbook/instrument/timeframe/regime/session.

## Operations

- data-quality incidents;
- timeouts/errors;
- queue pressure;
- average/p95 agent evaluation duration;
- candles/sec and memory metrics if available.

# 6.15 Live deployment/session reports

A live report must contain:

- deployment/account alias/environment;
- active policy/config/artifact revisions;
- session start/end and safety mode;
- market/data availability timeline;
- agent activity by instance/instrument/playbook;
- shadow/executable candidates;
- manual approvals;
- portfolio/risk decisions;
- broker submissions/fills/positions;
- management actions;
- reconciliation differences and resolutions;
- safety pauses and operator commands;
- realized/unrealized P&L and open risk;
- unresolved incidents at report end.

Support periods:

- current session;
- daily;
- arbitrary UTC range;
- deployment lifetime.

# 6.16 Candidate/trade explanation report

Provide a focused report for one candidate/trade:

```text
What was the market context?
What location/catalyst/trigger existed?
Which indicators confirmed or conflicted?
Which playbook state transitions occurred?
What was the proposed stop/target geometry?
What did calibration/ML decide and why?
What did sizing/portfolio decide?
What happened in execution?
How was the position managed?
What was the final outcome, MFE and MAE?
```

This is essential for debugging the new agent.

# 6.17 Query API

Add server-owned read APIs, preferably to `DashboardLive` for simulator/research and `LiveTradingHost` or a shared reporting API for live.

Suggested endpoints:

```text
GET /api/reports/simulations/{simulationId}
GET /api/reports/experiments/{experimentId}
GET /api/reports/live/sessions/{runtimeSessionId}
GET /api/reports/deployments/{deploymentId}?from=&to=
GET /api/reports/candidates/{candidateId}
GET /api/reports/positions/{positionId}
GET /api/reports/agents/{agentInstanceId}?from=&to=
GET /api/reports/rejection-reasons?...filters
```

Use pagination for event lists and server-side aggregation. Do not send full raw event history by default.

# 6.18 Dashboard UI

Add a dedicated reporting area rather than putting raw events into the chart panel.

Suggested views:

1. **Run Summary**
2. **Agent Funnel**
3. **Setup/Playbook Analysis**
4. **ML and Calibration**
5. **Risk and Portfolio**
6. **Execution and Management**
7. **Incidents and Data Quality**
8. **Candidate/Trade Explanation**

The chart replay should link to candidate IDs and state-transition markers, but the report data comes from PostgreSQL APIs.

# 6.19 CLI reports

Add commands to `BacktestRunner` or a separate `TradingHub.ReportRunner`:

```text
report simulation --id <uuid> --format json|markdown|csv
report experiment --id <uuid>
report live-session --id <uuid>
report deployment --id <uuid> --from ... --to ...
report candidate --id <uuid>
```

Markdown output should be human-readable and JSON should preserve structured fields. CSV is appropriate only for tabular sections, not the complete report.

# 6.20 Retention and partitioning

High-volume append-only tables need retention/partition planning.

- partition decision/setup-state/runtime-event tables by month or decision time where volume justifies it;
- retain normalized trades/orders/positions longer than verbose setup transitions;
- aggregate old observation windows before deleting details;
- preserve policy/artifact/config hashes indefinitely for reproducibility;
- never delete records required for an open position, active deployment, unresolved reconciliation or promoted artifact lineage.

Create a documented retention job with dry-run and metrics.

---

# 7. Write paths and performance

# 7.1 Persistence lanes

Reuse existing `PersistenceLane` design:

- **Critical:** order, fill, reservation, safety, reconciliation, checkpoint and operator actions;
- **Read:** reporting/status queries;
- **Research:** bulk candidate outcomes, experiment metrics and large batch analytics;
- **Lease:** dedicated advisory-lock connection.

Add an operational telemetry lane only if necessary; otherwise extend the existing critical/research data sources carefully. Do not let report queries exhaust live critical connections.

# 7.2 Bounded asynchronous writers

Extend the pattern in `OperationalBatchWriter`:

- bounded channel;
- batched inserts;
- cancellation and graceful drain;
- retry only transient failures;
- metrics for queue depth, latency and permanent failures;
- no silent drops of candidate/order/safety events.

For activity aggregates, coalescing is allowed because the aggregate row is the durable product. For critical event history, dropping is forbidden.

# 7.3 Transaction boundaries

Examples:

## Candidate creation

One transaction or idempotent batch for:

- decision key/evaluation if emitted;
- candidate key/candidate;
- initial agent/setup stage event.

## Portfolio approval

One critical transaction for:

- sizing evaluation;
- portfolio decision;
- reservation creation/event;
- candidate stage update.

## Submission/fill

Use idempotency keys and transactionally persist known state transitions. Unknown submission certainty triggers safety pause and reconciliation.

## Manual approval

Approval-state transition and audit event must commit atomically.

# 7.4 No serialization in the hottest path where avoidable

Build compact typed records. Serialize bounded diagnostic payloads once before enqueue or in the writer. Do not serialize entire `AnalysisSnapshot` or candle history.

# 7.5 Report read models

Do not execute dozens of unbounded joins for every Dashboard refresh. Create query services and, where needed, materialized views/read models for:

- candidate funnel summaries;
- rejection reason counts;
- daily live session summary;
- strategy/playbook performance;
- execution-quality summary;
- agent activity summary.

Refresh incrementally or on run completion.

---

# 8. Failure and recovery behaviour

# 8.1 Startup

- verify DB health and schema version;
- resolve required configuration revisions;
- resolve secret references;
- validate broker endpoints/account/instruments;
- acquire account lease;
- restore active deployment/checkpoint/positions/approvals;
- reconcile before broker writes.

If any required live step fails, start in degraded observe-only or fail startup according to explicit policy. Never silently fall back to a different account, endpoint or file configuration.

# 8.2 Runtime DB outage

## Simulation/research

- pause/retry jobs where safe;
- persist heartbeat/failure when DB returns;
- do not lose completed result manifests;
- child jobs must be recoverable.

## Live

- block new broker submissions if critical persistence is unavailable;
- continue receiving market data if safe and bounded;
- allow reconciliation/read-only diagnostics where possible;
- raise a safety event and visible incident;
- use emergency telemetry spool only for non-critical events;
- replay spool idempotently after recovery.

# 8.3 Host crash

On restart:

- recover active simulation jobs according to status/heartbeat;
- reconstruct live state from DB checkpoint + event tables;
- recover manual approvals and expire stale ones;
- reacquire account lease;
- reconcile broker state;
- do not automatically resubmit uncertain commands.

---

# 9. Migration phases

## Phase 0 — Baseline and inventory

- build/test current solution;
- record current file/config stores and schemas;
- inventory every `appsettings`, environment-variable and hard-coded endpoint use;
- document current DB migrations and store coverage;
- add guard tests proving no production host currently uses DB, then update them as work proceeds.

## Phase 1 — Bootstrap and host registration

- shared persistence bootstrap;
- wire `AddDBManagerPostgres` into all hosts;
- health/schema readiness;
- DatabaseAdmin CLI;
- no runtime cutover yet.

## Phase 2 — Broker/reference/config migration

- endpoint/environment/credential-reference schema;
- runtime profile schema;
- secret resolver;
- DB-backed broker/instrument catalog;
- immutable resolved configuration snapshot;
- import/seed utilities.

## Phase 3 — Policy, artifacts and research

- PostgreSQL repositories for policies/calibration/approval bundles;
- research store wiring;
- artifact payload/blob manifest policy;
- parity tests.

## Phase 4 — Simulator and experiment persistence

- PostgreSQL simulation job repository;
- experiment/profile/timeline persistence;
- progress throttling;
- output/blob manifests;
- recovery and parallel child jobs.

## Phase 5 — Live durable state

- PostgreSQL live persistence adapter;
- account advisory lease;
- DB manual approvals;
- decision/risk/execution/management/reconciliation wiring;
- dual-write parity;
- fail-closed outage behaviour.

## Phase 6 — Cutover

- `DualWrite` soak tests;
- compare file/DB projections;
- import existing data;
- switch reads to PostgreSQL;
- retain file emergency spool only;
- remove runtime file authority.

## Phase 7 — Structured telemetry

- runtime sessions;
- setup-state events;
- activity aggregate windows;
- cross-cutting lifecycle/incidents;
- explicit no-per-candle emission policy;
- wire simulator/live/agent pipeline boundaries.

## Phase 8 — Reports and UI

- simulation/experiment/live/candidate report builders;
- APIs;
- Dashboard views;
- CLI export;
- materialized/read summaries.

## Phase 9 — Retention, performance and operational validation

- partition/retention jobs;
- load tests;
- DB outage/recovery tests;
- backup/restore test;
- final documentation and cutover report.

---

# 10. Code-specific change map

Codex must inspect exact signatures before editing, but the intended ownership is:

## `DBManager.Abstractions`

Extend with:

- broker endpoint/environment/account query contracts;
- credential-reference records without secret payloads;
- runtime-profile store/query contracts;
- simulator/experiment persistence abstractions if they are not better placed in `Simulator`;
- runtime-session/activity/report query contracts;
- additional decision/setup-state records;
- blob manifest contracts.

Keep this project free from EF/Npgsql.

## `DBManager.Postgres`

Add:

- migrations/entities/configuration for new tables;
- PostgreSQL implementations;
- DB-backed repositories and adapters;
- reporting query services/materialized views;
- retention and import support;
- indexes and partitions.

## `Brokers`

- remove runtime dependence on hard-coded endpoint switches;
- accept resolved endpoint/account/instrument definitions;
- preserve seed defaults in an admin/seed layer only;
- never make DB calls from broker request methods.

## `DashboardLive`

- register PostgreSQL;
- replace file stores;
- resolve broker workspace/catalog through DB configuration;
- use DB job/report services;
- expose reporting APIs;
- keep replay blobs server-owned.

## `Simulator`

- add PostgreSQL job repository implementation or adapter;
- persist run/profile/config/timeline identity;
- emit meaningful state/candidate/stage/outcome events;
- aggregate unchanged observations;
- register replay/blob manifests.

## `BacktestRunner`

- use shared DI/persistence construction rather than manually `new FileSimulationJobRepository`;
- support profile/experiment IDs from DB;
- add report commands or call shared report service;
- preserve offline/file-only mode only as explicitly named development compatibility if required.

## `QuantResearch.Training` and `QuantResearchRunner`

- wire `IResearchStore` and artifact repositories;
- persist folds, embargo, calibration stages and artifacts;
- emit lifecycle/failure records;
- never retrain from evaluation data.

## `LiveTradingHost`

Replace direct registrations for:

- file calibration/profile/bundle/job stores;
- `FileTradingAccountLease`;
- `FileLiveTradingPersistence`;
- in-memory-only manual approval durability.

Load active deployment/runtime configuration from DB. Keep only host bind/bootstrap and secret-provider bootstrap outside DB.

## `LiveTrading`

- map runtime events to specialized DB stores through abstractions;
- add telemetry at orchestration boundaries;
- add activity aggregation;
- preserve single account-wide writer and safety semantics;
- do not add DB dependencies to agent calculation code.

## `TradingJournal`

- preserve compatibility;
- optionally adapt entries to typed telemetry;
- do not use it as the primary durable report database;
- deprecate message-only event usage where typed records exist.

## Structural Confluence Agent work

- include stable playbook/setup IDs and state transitions in bounded diagnostics;
- do not write directly to PostgreSQL;
- simulator/live orchestration maps transitions to telemetry;
- report CCI/liquidity/S&D/PA evidence through bounded feature snapshots.

---

# 11. Database indexes and constraints

At minimum add indexes for common report paths:

```text
(runtime_session_id, occurred_at)
(agent_instance_id, occurred_at)
(instrument_id, occurred_at)
(strategy_id, playbook_id, occurred_at)
(candidate_id, occurred_at)
(deployment_id, occurred_at)
(simulation_id, occurred_at)
(reason_code, occurred_at)
(status, heartbeat_at)
```

Use unique idempotency constraints for:

- decision ID;
- candidate ID;
- client-order ID/order-command key;
- broker transaction key;
- simulation revision updates;
- artifact content hash;
- configuration hash/revision;
- one active endpoint revision per environment/kind;
- one active deployment per broker account;
- one active lease per broker account.

Use JSONB only for bounded evolving details. Promote frequently filtered values to columns.

---

# 12. Tests

# 12.1 Database integration tests

Use the existing Testcontainers PostgreSQL pattern in `DBManager.Tests`.

Test:

- migrations from empty DB;
- broker endpoint/account/instrument resolution;
- secret references do not contain values;
- runtime-profile revision/approval;
- artifact payload/hash behaviour;
- simulation job optimistic concurrency and recovery;
- live checkpoint/reconciliation recovery;
- advisory account lease across two data sources/process simulations;
- manual approval exactly-once consumption;
- report queries and partitions;
- backup and restore sanity.

# 12.2 Migration/parity tests

- import existing file records idempotently;
- malformed file quarantine;
- no secret import;
- dual-write produces equivalent DB/file projections;
- cutover reads return same active policy/artifact/job state.

# 12.3 No-per-candle telemetry tests

Create a long no-signal simulation and prove:

- candle count may be very large;
- detailed agent/setup event count remains near zero/bounded;
- activity aggregate count follows time/run windows, not candle count;
- one state change creates one durable event;
- repeated unchanged state does not create duplicates.

# 12.4 Agent report tests

For known synthetic scenarios verify reports explain:

- liquidity swept/reclaimed;
- CCI confirmation or conflict;
- demand/supply confluence;
- candidate generation;
- calibration/meta-label outcome;
- portfolio rejection or approval;
- virtual/live execution;
- management changes;
- MFE/MAE/final outcome.

# 12.5 Safety tests

- DB failure before order submission blocks writes;
- unknown submission certainty pauses trading;
- failed critical persistence does not report success;
- report writer failure cannot bypass execution safety;
- no access token/control token appears in DB rows, JSON payloads, logs or exception messages;
- runtime never falls back from Live to Demo or between accounts/endpoints silently.

# 12.6 Performance tests

Measure:

- agent hot-path overhead with telemetry enabled/disabled;
- batch queue depth/latency;
- simulator throughput before/after;
- report query performance on realistic volumes;
- live critical-pool isolation from report queries;
- bounded memory during long runs.

---

# 13. Acceptance criteria

The work is complete only when all of the following are true:

1. Production hosts register and use PostgreSQL stores.
2. Broker base URLs/endpoints/environments and mappings are DB-owned and versioned.
3. Secrets are resolved by reference and never stored/logged as plaintext.
4. Runtime/strategy/policy/artifact configuration is revisioned, approved, resolved and hash-pinned.
5. Simulator/experiment jobs survive host restart from PostgreSQL.
6. Live state, lease, approvals, reconciliation, orders and management survive restart from PostgreSQL.
7. File repositories are no longer the source of truth in final mode.
8. Large external blobs have DB manifests and hashes.
9. Agent and pipeline telemetry is typed and correlated.
10. No per-candle detailed logging occurs.
11. Ordinary observations are represented by bounded aggregate windows.
12. Reports explain candidate formation, ML, risk, execution, management and outcomes.
13. Simulation, research, shadow and live modes use the same reporting model with explicit mode labels.
14. DB outage behaviour is tested and live execution remains fail-closed.
15. Existing legacy/improved strategy behaviour remains unchanged except for persistence/reporting side effects.
16. Structural Confluence profile experiments can be compared from DB-backed reports.
17. Existing and new tests pass.
18. Codex provides a final migration/cutover report and does not create a zip archive.

---

# 14. Explicit non-goals

Do not:

- store every candle as a log event;
- serialize complete chart snapshots into diagnostic JSON;
- replace domain persistence with Serilog text files;
- store credentials in PostgreSQL plaintext;
- query PostgreSQL from indicator, annotation or agent loops;
- use one mutable global settings row without revision/history;
- remove idempotency, reservation or reconciliation safety;
- put raw client-controlled filesystem paths into report APIs;
- delete all file/blob support when large replay/candle data still needs efficient storage;
- let telemetry failure silently permit broker execution;
- ask the user to zip or repackage the repository.

---

# 15. Required final Codex report

At completion, produce a Markdown report in the repository describing:

1. files/projects changed;
2. migrations added and schema diagram/summary;
3. which settings moved to DB and which bootstrap values remain outside;
4. secret handling and redaction tests;
5. file-to-DB import results;
6. dual-write/parity results;
7. final persistence mode for each host;
8. event emission policy and proof that no per-candle logging occurs;
9. report APIs/UI/CLI added;
10. performance measurements;
11. DB outage/recovery and live-safety test results;
12. backup/restore validation;
13. commands used to build, test, migrate and run;
14. remaining risks or deferred work.

Do not claim success for unrun tests or unverified cutover steps.

---

# 16. Final design summary

The intended result is:

```text
PostgreSQL owns durable identity, configuration, runtime state and searchable history.
Secrets remain outside PostgreSQL and are referenced safely.
Large immutable data remains managed blob storage with DB manifests.
Agents do not talk to PostgreSQL and do not log every candle.
Meaningful state changes and decisions become typed domain records.
Ordinary observations become bounded aggregate windows.
Reports reconstruct what happened from setup detection through ML, risk,
execution, management and final outcome in simulator, shadow and live modes.
```
