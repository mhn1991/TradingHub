# TradingHub Agent Lifecycle Automation and Plug-and-Play Live Deployment — Final Report

## 1. Files and projects changed

- `Agent`: generic `AgentDefinition`, canonical type IDs, catalogue/builders, and Structural Confluence registration.
- `TradingPolicies`: immutable `ResolvedAgentPackage` and PostgreSQL-backed exact-revision resolution.
- `Simulator` and `QuantResearchRunner`: profile manifests, bounded experiment orchestration, held-out ranking, shortlisting, promotion candidates, and parity inputs.
- `DBManager.Abstractions`, `DBManager.Postgres`, and `TradingHub.Persistence.Postgres`: lifecycle contracts/entities/stores, encrypted broker credential authority, experiment persistence, telemetry, and reports.
- `LiveTrading` and `LiveTradingHost`: dynamic registries, supervisor lifecycle, deployment orchestration, command processing/recovery, reconciliation gates, APIs, and SignalR notifications.
- `Dashboard`: Agent Library, guided deployment wizard, active deployment/lifecycle controls, and reporting integration.
- `TradingHub.AdminCli`: thin lifecycle API client with deployment and Agent commands.
- Focused tests were added or updated in `Simulator.Tests`, `LiveTrading.Tests`, and `TradingHub.UnitTests`.

## 2. Migrations added

- `20260718165515_Phase11AgentLifecycleAutomation` adds durable deployments, deployment agents, commands/events, Agent instances/events, permissions, parity certifications, broker-account leases, runtime sessions, and pinned position/package data.
- The migration is applied. The database reports 12 applied migrations, with Phase 11 current.
- Earlier Phase 8–10 migrations in the same working tree provide the encrypted broker credential vault, PostgreSQL runtime/reporting authority, and setup telemetry used by this phase.

## 3. Static/file paths

- PostgreSQL is authoritative for policy revisions, broker accounts/instruments, encrypted broker credentials, permissions, certifications, deployments, commands, Agent state, leases, and runtime/report state.
- `.env.integration` broker secrets were migrated to encrypted database records and scrubbed; runtime broker resolution no longer depends on that file.
- `LivePolicyBundles` and static market configuration remain only as explicit bootstrap/import compatibility paths. They are not authoritative for dynamic deployment state.
- The local database locator remains `.state/tradinghub.database.json`; it contains connection configuration, not broker secrets.

## 4. Agent catalogue entries

- `legacy-progressive`
- `improved-progressive`
- `structural-confluence`

Canonical exact matching, schema validation, duplicate rejection, unknown-type rejection, deterministic legacy migration, package hashing, and source neutrality have dedicated guards.

## 5. Simulator → profile → parity → deployment flow

1. Research creates an immutable simulation profile revision in PostgreSQL.
2. An experiment resolves exact revisions, calculates learning/warm-up/embargo/test windows, freezes artifacts, and evaluates held-out data in bounded parallel groups.
3. The server applies configurable hard gates and a documented stability score, persists the score breakdown, shortlists candidates, and creates deterministic promotion-candidate records pinning profile revision/hash, agent-definition hash, dataset hash, timeline, and artifact hashes.
4. An approved policy revision resolves to one immutable `ResolvedAgentPackage` used by simulator, parity, live shadow, Demo, and live execution paths.
5. Decision-parity certification is persisted against the exact configuration hash and source commit.
6. Deployment preflight checks PostgreSQL authority, permissions, parity, broker/account/instrument mapping, package/artifact compatibility, warm-up readiness, lease ownership, reconciliation, and execution switches before start.

Promotion candidates never receive executable permission automatically.

## 6. APIs and Dashboard

- APIs cover Agent types, policies/revisions, permissions/certification, broker accounts/instruments, deployment preflight/start/list/status, deployment Agent assignment, pause/resume/reconcile/stop, and Agent pause/resume/drain/stop/replace.
- Mutations require control authorization, actor/reason metadata, expected versions, and idempotency keys; failures return structured errors.
- SignalR emits deployment changes.
- Dashboard additions include `AgentLibraryPanel`, `DeploymentWizard`, `ActiveDeploymentsPanel`, lifecycle controls, experiment/profile panels, and reporting views. The wizard selects stored revisions and deployment scope only; it cannot edit strategy behaviour.
- `TradingHub.AdminCli` exposes equivalent operational lifecycle commands over the same APIs.

## 7. Dynamic live lifecycle

The host preloads durable state and can register, warm, reconcile, start, pause, resume, drain, stop, and recover Agent instances without a process restart. Commands and resulting events are persisted transactionally and are safe to retry. Recovery resolves the recorded package revision and fails closed if reconciliation is not clean.

## 8. Drain and hot-swap semantics

- Drain stops new entries while package-pinned position management continues.
- Stop/replacement does not abandon managed positions.
- Replacement prepares the new exact revision in registry history, stages the supervisor swap, atomically activates the new revision, and retains the old package for positions opened under it.
- One executable owner per broker account/instrument is enforced by a PostgreSQL uniqueness/lease boundary; shadow assignments do not acquire broker-write ownership.

## 9. Database and reporting

- Lifecycle commands/events, assignments, Agent state, runtime sessions, telemetry, candidates, orders, positions, and package pins are stored in PostgreSQL.
- Experiment snapshots include immutable shortlist/promotion-candidate data; baseline comparisons are also projected into structured comparison rows.
- Deployment reports include lifecycle events and reasons alongside telemetry. Simulation, experiment, runtime session, deployment, candidate, position, Agent, and rejection-reason report endpoints are available.
- Reporting uses meaningful state transitions and activity windows; no per-candle database logging was introduced.

## 10. Verification results

- `Simulator.Tests`: catalogue, promotion, Structural experiment/ranking selection passed 19/19; the broader experiment/profile selection passed 24/24 before the additional ranking guard was added.
- `LiveTrading.Tests`: market-analysis readiness and policy-registry selection passed 12/12.
- Live parity/capacity/deployment/runtime/shadow-outcome selection passed 18/18.
- `TradingHub.UnitTests`: broker safety and reconciliation selection passed 3/3.
- `LiveTradingHost`, `DBManager.Postgres`, `TradingHub.Persistence.Postgres`, and `TradingHub.AdminCli` builds passed with zero warnings and zero errors.
- Dashboard `npm run typecheck` passed.
- `git diff --check` passed.

## 11. Performance measurement

The 80-Agent capacity test completed one shared-snapshot epoch in **12.452 ms**. Evaluation latency was p50 **6.670 ms**, p95 **7.840 ms**, and p99 **8.166 ms**; allocated memory was **1,587,424 bytes**, GC collections were **0/0/0**, and peak mailbox depth was **1**.

## 12. Known limitations

- Docker is unavailable in this environment, so Testcontainers PostgreSQL concurrency/failure-injection tests could not be run here. The real PostgreSQL migration and focused persistence paths were exercised instead.
- No external OANDA Demo/live order was submitted during verification; doing so would create an external trading side effect. Broker integration remains gated behind stored credentials, permissions, reconciliation, and execution switches.
- Optional automatic Shadow scheduling and automated drift/challenger generation are not enabled by default. Candidates are automatically shortlisted, while Shadow deployment and challenger replacement remain controlled through the durable API/UI workflow. Automatic executable promotion remains prohibited.

## 13. Manual commands

The migration is already applied. For another environment:

```bash
dotnet run --project DBManager.Cli -- migrate
dotnet run --project LiveTradingHost
npm --prefix Dashboard run dev
dotnet run --project TradingHub.AdminCli -- --help
```

The target environment must provide `.state/tradinghub.database.json` and the database-held encrypted broker credential records.

## 14. Packaging

No zip, archive, or project resend is requested or required.
