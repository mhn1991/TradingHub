# TradingHub PostgreSQL Runtime Migration and Structured Reporting — Final Report

Date: 2026-07-18  
Cutover target: local TradingHub PostgreSQL deployment  
Final runtime persistence mode: `PostgresOnly`

The PostgreSQL runtime cutover and structured reporting implementation are in place and are operating against the configured database. Direct database validation, file-import parity, backup/restore, application builds, and focused live/simulator tests succeeded. Full blueprint acceptance is not yet claimed: the Testcontainers suite could not run because this machine has no Docker socket, and the complete outage-injection, synthetic-report, and before/after performance matrices remain deferred.

## 1. Files and projects changed

The implementation is grouped by responsibility below. Generated EF migration designer files and the model snapshot are included with their corresponding migrations.

- `DBManager.Abstractions`: persistence modes, lane contracts, decision/configuration contracts, and database-facing abstractions.
- `DBManager.Postgres`: EF Core model, PostgreSQL stores, migration history, health/schema checks, backup/restore, retention, encrypted broker credential storage, runtime observability, simulation/experiment entities, and report queries.
- `TradingHub.Persistence.Postgres`: PostgreSQL adapters for application bootstrap, calibration, policies, simulations, experiments, live leases/checkpoints/journal/approvals, and runtime telemetry.
- `TradingObservability.Abstractions`: environment-neutral telemetry and seven structured operational report contracts.
- `DBManager.Cli`: bootstrap, migrate, health, schema version, file import, canonical parity, broker import/verification, retention, backup, and restore-test commands.
- `TradingHub.ReportRunner`: new JSON/Markdown report exporter with explicit tabular CSV sections.
- `DashboardLive` and `LiveTradingHost`: approved database runtime-profile loading, `PostgresOnly` registration, report APIs, and live runtime observability.
- `BacktestRunner`, `QuantResearchRunner`, and `QuantResearch.Training`: PostgreSQL-backed job, artifact, bundle, profile, and experiment paths.
- `Dashboard`: structured reporting panel plus simulation/experiment views and types.
- `LiveTrading` and `LiveTrading.Tests`: bounded shadow outcome persistence, stable correlations, recovery behavior, and no-unchanged-candle write proof.
- `Simulator`, `Simulator.Tests`, and `Simulator.Experiments`: PostgreSQL-compatible experiment persistence and structural experiment coverage.
- `docs/postgresql-retention.md` and this report: operator-facing retention/cutover documentation.

Primary entry points include `DBManager.Postgres/TradingHubDbContext.cs`, `DBManager.Postgres/Reporting/PostgresTradingReportQueryService.cs`, `TradingHub.Persistence.Postgres/`, `DashboardLive/TradingReportApi.cs`, `LiveTradingHost/Api/TradingReportApi.cs`, `Dashboard/src/components/ReportingPanel.vue`, and `TradingHub.ReportRunner/Program.cs`.

## 2. Migrations and schema summary

Eleven EF Core migrations are applied. The current migration is `20260718155041_Phase10PostgresAuthoritiesAndSetupTelemetry` using EF Core `10.0.10`.

| Phase | Migration | Main responsibility |
|---|---|---|
| History | `20260718021927_InitialSchemaHistory` | PostgreSQL schema history/bootstrap |
| 1 | `20260718024507_Phase1ReferenceAndConfig` | broker, instrument, account, versioned configuration |
| 2 | `20260718025737_Phase2Decision` | evaluations, candidates, setup/stage events |
| 3 | `20260718030427_Phase3Risk` | sizing, portfolio decisions, reservations |
| 4 | `20260718035342_Phase4Execution` | orders, fills, broker transactions, positions |
| 5 | `20260718040411_Phase5Management` | leases, checkpoints, reconciliation, management state |
| 6 | `20260718041133_Phase6Research` | research, folds, calibration and analytics |
| 7 | `20260718092730_Phase7Backup` | backup records and restore-test status |
| 8 | `20260718144140_Phase8BrokerCredentialVault` | encrypted broker credential payloads |
| 9 | `20260718151718_Phase9RuntimePersistenceAndReporting` | simulations, experiments, sessions, telemetry, live journal/approvals |
| 10 | `20260718155041_Phase10PostgresAuthoritiesAndSetupTelemetry` | runtime profiles, revisions, operational authority/telemetry additions |

Logical schema flow:

```text
broker/reference + approved revisions
                 |
                 v
policy/artifact -> deployment/runtime profile -> runtime session
                                             |          |
                                             v          v
                              evaluation -> candidate -> risk/reservation
                                                          |
                                                          v
                                      order -> fill -> position -> management
                                         \        |          /
                                          structured telemetry

simulation profile -> experiment -> simulation job -> manifest/blob references
                                      |
                                      +-> progress/status timeline/activity windows

operations: lease + checkpoint + reconciliation + approval + live journal
security: credential reference -> encrypted broker credential payload
maintenance: backup record + retention policy
```

Foreign keys, unique business/idempotency keys, optimistic revisions, hashes, and lane-specific pooled data sources protect the main state transitions. Large outputs remain external blobs addressed by database manifests, hashes, media type, size, and retention metadata.

## 3. Database-owned settings and external bootstrap values

PostgreSQL now owns and versions:

- broker environments, base endpoints, account settings, credential references, and instrument mappings;
- approved Dashboard and Live Host runtime-profile revisions;
- policy revisions, calibration artifacts/bundles, promotion and deployment state;
- simulation profiles, jobs, progress, experiments, comparisons, and output manifests;
- research runs and fold/calibration metadata;
- live sessions, leases, checkpoints, reconciliation, approvals, execution/position/management state;
- typed telemetry and aggregate activity windows.

`DashboardLive/appsettings.json` and `LiveTradingHost/appsettings.json` are intentionally empty JSON objects after cutover. They are not runtime authorities.

The minimum bootstrap values intentionally remain outside PostgreSQL:

- the PostgreSQL connection/bootstrap document at `.state/tradinghub.database.json`;
- the local broker-credential encryption key file under `.state`;
- process-local logging/hosting values needed before database connectivity exists.

These bootstrap files must be provisioned and permissioned by the deployment environment; they are not application configuration fallbacks.

## 4. Secret handling and redaction validation

Broker credentials from `.env.integration` were imported into PostgreSQL as encrypted payloads. Runtime resolution is `credential reference -> encrypted database row -> local protector`, so hosts no longer depend on broker environment variables. The encryption key is deliberately stored separately from the encrypted database payload.

The credential APIs return references and safe metadata, not secret values. Safe broker configuration is loaded independently from secret resolution. Report output masks broker identifiers where appropriate and never projects credential payload columns.

Validation performed:

- database credential resolution verified for OANDA/DEMO and Binance/TESTNET as enabled and IG/DEMO as disabled;
- `.env.integration` contains no remaining OANDA, Binance, or IG credential entries;
- no `AccessToken`, `ApiKey`, `SecretKey`, `ControlToken`, or `Password` fields were found in source `appsettings*.json` files;
- generated simulation Markdown was inspected and contained no secrets.

Not yet performed: a repository-wide captured-log/exception corpus scan under injected broker and database failures. Therefore the blueprint's exhaustive redaction test is not claimed.

## 5. File-to-database import results

The legacy inventory found 70 valid simulation jobs and three already-quarantined malformed files. The applied import inserted or updated all 70 valid jobs with no conflicts. A subsequent dry run reported:

- discovered: 70;
- inserted/updated: 0;
- already current: 70;
- conflicts: 0;
- quarantined: 3.

This proves idempotency for the present simulation-job corpus. Legacy calibration artifacts, simulation profiles/experiments, policy profiles, calibration bundles, and live-persistence directories contained zero source records, so those adapters had nothing to mutate. Runtime configuration was seeded through the approved database revision path rather than copied from host `appsettings`.

## 6. Parity and cutover results

Canonical JSON hashing sorts object/dictionary keys while preserving array order. Final parity compared all 70 legacy simulation-job projections with PostgreSQL:

- compared: 70;
- discrepancies: 0;
- parity: `true`;
- persistence mode: `PostgresOnly`.

A byte-order mismatch seen during initial validation was traced to dictionary property order, not semantic data loss; canonical semantic hashing fixed the verifier. Dual-write was not exercised after final cutover and is reported by the verifier as `not_applicable_after_cutover`. File projections are retained only for import/recovery evidence and large blob storage—not as runtime authority.

## 7. Final persistence mode by host/process

| Host/process | Final authority | Notes |
|---|---|---|
| `DashboardLive` | `PostgresOnly` | loads approved `Dashboard/default`; file settings are empty |
| `LiveTradingHost` | `PostgresOnly` | loads approved `LiveHost/default`; live durable state is PostgreSQL-backed |
| `BacktestRunner` | PostgreSQL | job and calibration repositories are PostgreSQL-backed |
| `QuantResearchRunner` | PostgreSQL | job, experiment, profile, artifact, and bundle paths are PostgreSQL-backed |
| Dashboard report UI | PostgreSQL API read models | no direct database or filesystem access |
| `TradingHub.ReportRunner` | PostgreSQL read models | standalone bounded report exporter |

There is no automatic PostgreSQL-to-file, Live-to-Demo, broker-account, or endpoint fallback.

## 8. Event emission policy and no-per-candle proof

`ILogger` remains process diagnostics. Typed trading telemetry records durable business/operational state changes with runtime-session, deployment, simulation, experiment, candidate, position, order, agent, and correlation identifiers.

Detailed rows are emitted for material transitions: setup lifecycle changes, candidate funnel stages, calibration/risk decisions, order/fill/position transitions, management changes, safety/data-quality incidents, persistence changes, and session lifecycle. Ordinary warmup/no-setup/hold observations are accumulated into bounded `AgentActivityWindow` rows and reason counts.

No ordinary completed-candle hold or mark-to-market row is written. `CompletedCandles_WithoutStateChange_DoNotPersistDetailedRows` processes 1,000 completed candles and asserts that detailed persistence remains unchanged. The complete `LiveShadowOutcomeServiceTests` fixture passed 6/6 in 159 ms and also covers state-changing persistence, idempotent candidate identity, recovery, ambiguity handling, and structural-policy pinning.

Retention defaults to dry-run, 90 days for eligible low-severity uncorrelated detail and 365 days for aggregate activity. Normalized decision, execution, safety, and correlated audit records are not retention targets.

## 9. Report APIs, UI, and CLI

Both `DashboardLive` and `LiveTradingHost` expose bounded structured reports for:

- simulation;
- experiment;
- live session;
- deployment;
- candidate;
- position;
- agent;
- rejection-reason frequencies.

Reports join normalized state, timelines, incidents, reason aggregates, execution/management outcomes, reproducibility hashes, and completeness warnings. Query limits prevent unbounded event retrieval, and API callers cannot provide raw filesystem paths.

The Dashboard Reporting panel provides all seven identity-based reports with optional time ranges and structured sections. `TradingHub.ReportRunner` exports JSON or Markdown, and exports CSV only when the caller explicitly selects a tabular section. A real Markdown report for a simulation with 84,686 processed candles was generated successfully and displayed human-readable status/reason values.

## 10. Performance measurements

Only measurements actually taken are reported:

| Operation | Observed result |
|---|---|
| PostgreSQL health query | healthy; 240 ms reported latency |
| canonical parity over 70 jobs | 5.6 s command wall time; zero discrepancies |
| structured simulation Markdown export | approximately 2.5 s cold CLI wall time for a job with 84,686 processed candles |
| retention dry run | 1,638 ms; zero eligible/deleted rows |
| six shadow outcome tests including 1,000 unchanged candles | 159 ms test duration |

The application uses independent critical/read/research/lease data sources, bounded report event lists, aggregate windows, and no per-candle detailed writes. A controlled before/after simulator benchmark, telemetry-enabled/disabled agent benchmark, queue-depth/latency soak, report-query concurrency isolation test, and long-run memory profile were not run and remain required before high-volume production sizing.

## 11. Database outage, recovery, and live-safety results

Focused safety/recovery tests succeeded:

- shadow broker/coordinator, manual approval, and live checkpoint/journal tests: 14/14 passed in 408 ms;
- live shadow outcome/recovery tests: 6/6 passed in 159 ms;
- structural experiment/confluence/evidence tests: 16/16 passed in 824 ms;
- unchanged-candle detail writes remain bounded;
- manual approvals remain fingerprint-bound and exactly-once;
- checkpoint hashes and live journal envelopes are validated;
- shadow execution cannot call broker mutation methods.

The live host uses critical PostgreSQL persistence and does not fall back to files or another broker environment. Direct database health, profile, secret-reference, import/parity, and report queries succeeded.

The existing seven-test `PostgresPersistenceTests` fixture was attempted twice, including outside the filesystem sandbox, but all seven failed during one-time setup because Docker is unavailable and `/var/run/docker.sock` does not exist. No test body ran; this is an environmental failure, not a passing database test. Comprehensive runtime outage injection—database loss immediately before submission, unknown submission certainty, and simultaneous report-load/critical-write isolation—was not run, so the full fail-closed acceptance item remains unverified.

## 12. Backup and restore validation

An actual custom-format PostgreSQL backup completed successfully:

- backup ID: `5b7dc308-8371-44b8-ac82-65f76849b02a`;
- artifact: `.state/backups/5b7dc308837144b8ac8265f76849b02a.dump`;
- status: succeeded.

The CLI then created a scratch database, restored this artifact, ran its sanity check, recorded success, and removed the scratch database. Restore failure handling records creation/restore/sanity/cleanup errors and does not report false success.

## 13. Build, migration, validation, and run commands

No credentials are embedded in these commands; they use the local bootstrap document.

```bash
dotnet run --project DBManager.Cli/DBManager.Cli.csproj -- migrate
dotnet run --project DBManager.Cli/DBManager.Cli.csproj -- seed-reference
dotnet run --project DBManager.Cli/DBManager.Cli.csproj -- import-broker-credentials
dotnet run --project DBManager.Cli/DBManager.Cli.csproj -- verify
dotnet run --project DBManager.Cli/DBManager.Cli.csproj -- health
dotnet run --project DBManager.Cli/DBManager.Cli.csproj -- schema-version
dotnet run --project DBManager.Cli/DBManager.Cli.csproj -- import-files --apply
dotnet run --project DBManager.Cli/DBManager.Cli.csproj -- import-files
dotnet run --project DBManager.Cli/DBManager.Cli.csproj -- verify-parity
dotnet run --project DBManager.Cli/DBManager.Cli.csproj -- retention
dotnet run --project DBManager.Cli/DBManager.Cli.csproj -- backup
dotnet run --project DBManager.Cli/DBManager.Cli.csproj -- restore-test --id <backup-id>

dotnet build TradingHub.ReportRunner/TradingHub.ReportRunner.csproj --no-restore -m:1
dotnet build DashboardLive/DashboardLive.csproj --no-restore -m:1
dotnet build LiveTradingHost/LiveTradingHost.csproj --no-restore -m:1
(cd Dashboard && npm run build)

dotnet test LiveTrading.Tests/LiveTrading.Tests.csproj --no-restore -m:1 \
  --filter FullyQualifiedName~LiveShadowOutcomeServiceTests
dotnet test LiveTrading.Tests/LiveTrading.Tests.csproj --no-restore -m:1 \
  --filter "FullyQualifiedName~ManualApprovalStoreTests|FullyQualifiedName~LiveTradingPersistenceTests|FullyQualifiedName~ShadowBrokerClientTests|FullyQualifiedName~ShadowExecutionCoordinatorTests"
dotnet test Simulator.Tests/Simulator.Tests.csproj --no-restore -m:1 \
  --filter "FullyQualifiedName~StructuralExperimentTests|FullyQualifiedName~StructuralConfluenceTests|FullyQualifiedName~StructuralEvidencePolicyTests"

dotnet run --project TradingHub.ReportRunner/TradingHub.ReportRunner.csproj -- \
  report simulation --id <uuid> --format markdown --output <path>
```

## 14. Remaining risks and deferred work

The runtime cutover is implemented, but the following evidence is still required before declaring every blueprint acceptance criterion complete:

1. Run `DBManager.Tests/PostgresPersistenceTests` on a machine with Docker/Testcontainers and confirm empty-database migrations and independent lane behavior.
2. Add/run the full database integration matrix for endpoint/account resolution, revision approval, artifacts, optimistic recovery, cross-connection lease ownership, exactly-once PostgreSQL approvals, report queries/partitions, and backup sanity.
3. Add synthetic end-to-end report assertions for sweep/reclaim, CCI, supply/demand, calibration/meta-label, risk, execution, management, MFE/MAE, and final outcomes.
4. Run explicit database-outage and unknown-submission-certainty tests at the live broker boundary, plus captured-log/exception secret scans.
5. Run the before/after throughput, telemetry overhead, queue/pool isolation, report concurrency, and bounded-memory performance matrix.
6. Exercise a supervised staging/live restart and reconciliation drill. No production broker execution or production deployment was performed during this task.
7. Schedule retention and backup/restore verification operationally; the code defaults to safe dry-run for retention.

No zip archive was created.
