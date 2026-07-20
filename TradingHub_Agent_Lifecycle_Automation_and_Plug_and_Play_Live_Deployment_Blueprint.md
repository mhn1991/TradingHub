# TradingHub Agent Lifecycle Automation and Plug-and-Play Live Deployment
## Production Implementation Blueprint for Codex

> Repository: the existing local `TradingHub` repository.
>
> This is a full production implementation task, not an isolated prototype.
>
> **Do not zip, copy, or repackage the repository. Work directly in the existing local checkout.**
>
> Preserve existing safety behaviour, deterministic simulation behaviour, tests, public contracts where practical, and backward compatibility while performing the migration in controlled phases.

---

# 1. Objective

Implement a unified, automated agent lifecycle so an agent configuration discovered and validated in the simulator can be promoted and deployed to live market data without reconstructing its strategy in a second configuration system.

The intended operator workflow is:

```text
Create simulator experiment
    ↓
Generate and run several immutable strategy profiles in parallel
    ↓
Train calibration/meta-model artifacts on an explicit past learning window
    ↓
Apply embargo and evaluate on a held-out test window
    ↓
Rank stable profiles and create promotion candidates
    ↓
Freeze one exact agent package and its artifacts
    ↓
Run simulator/live decision-parity certification
    ↓
Deploy automatically to live shadow
    ↓
Validate live-shadow outcomes and drift
    ↓
Approve for OANDA Demo manual/automatic execution
    ↓
Select the configured agent in the Dashboard and press Start
```

The live operator must be able to see configured/approved agents in the Dashboard, select:

- broker account;
- exact agent profile revision;
- one or more compatible instruments;
- activation mode (`Shadow`, `ManualApproval`, or `Automatic`);

and start the deployment without:

- manually copying JSON;
- rebuilding `appsettings.json`;
- editing `LiveMarkets` or `LivePolicyBundles`;
- restarting the host;
- reconstructing the strategy from a second set of settings;
- exposing broker credentials to the browser.

The Agent must remain environment-neutral. It receives normalized market/account context and must not know whether the source is:

- historical simulator data;
- replayed live data;
- current OANDA Demo data;
- current real-account data.

Environment, data-source, execution-mode, credentials, broker APIs, persistence, and operator permissions remain outside the Agent project.

---

# 2. Current repository facts Codex must respect

## 2.1 The core Agent contract is already environment-neutral

`Agent/Abstractions/ITradingAgent.cs` exposes:

```csharp
Task<AgentDecision> EvaluateAsync(
    AgentMarketContext context,
    CancellationToken cancellationToken = default);
```

It does not expose a simulator/live flag and does not allow broker writes. Preserve this principle.

Do **not** add fields such as:

```csharp
bool IsLive
bool IsSimulator
BrokerEnvironment Environment
IBrokerClient Broker
```

to `ITradingAgent`, strategy options, evidence packets, or playbooks.

## 2.2 Simulator and live already attempt to share strategy construction

Relevant files include:

- `Simulator/Engine/StrategySimulationSession.cs`
- `Simulator/Services/BacktestApplicationService.cs`
- `LiveTrading/Agents/LiveAgentFactory.cs`
- `LiveTrading/Agents/AgentSupervisor.cs`
- `TradingCore/Pipeline/*`

`LiveAgentFactory` explicitly states that it builds a live strategy in the same way as `StrategySimulationSession.Create()`. Keep and strengthen this objective.

## 2.3 Strategy discovery is still duplicated and hard-coded

`Simulator/Services/BacktestApplicationService.CreateDefaultStrategies()` currently switches over only:

```text
legacy
improved
```

`BacktestRequest.Validate()` and `StrategyInstrumentAssignment` also hard-code those two values.

The live host separately resolves `ProgressiveAgentKind` and constructs agents through `LiveAgentFactory`.

This duplication must be removed. The Structural Confluence Agent and future agents must not require edits to many unrelated switch statements.

## 2.4 `TradingPolicyProfile` is not yet generic enough

`TradingPolicies/TradingPolicyProfile.cs` currently contains:

```csharp
ProgressiveAgentKind AgentKind
ProgressiveStrategyOptions AgentOptions
```

That directly couples the portable policy format to progressive agents. A Structural Confluence Agent needs a generic, versioned agent definition while retaining backward-compatible deserialization for existing profiles.

## 2.5 Live assignments are currently startup configuration

`LiveTrading/Configuration/LiveMarketDefinition.cs` contains:

- `LiveMarketDefinition`;
- `LiveStrategyAssignment`;
- `StrategyActivationMode`.

`LiveTradingHost/LiveEngineHostedService.RegisterAgentsAsync()` reads:

- `IOptions<LiveMarketUniverseOptions>`;
- `IOptions<Dictionary<string, LivePolicyBundleOptions>>`;

and registers agents during host startup.

This means adding/removing a configured agent normally requires configuration changes and a restart. Replace this with database-backed dynamic deployment orchestration.

## 2.6 The Dashboard already monitors live operation but cannot deploy configured agents

Relevant frontend files include:

- `Dashboard/src/components/LiveDemoPanel.vue`;
- `Dashboard/src/composables/useLiveEngineStatus.ts`;
- `Dashboard/src/components/LiveDecisionChart.vue`.

The current Live Demo page supports:

- status;
- pause/resume;
- reconcile;
- manual candidate approval/rejection;
- position close/reduce/stop commands;
- agent status display.

It does not provide an Agent Library, deployment preflight, or dynamic start/stop of configured agents. Add those capabilities without weakening existing safety controls.

## 2.7 PostgreSQL already contains useful policy/deployment foundations

Relevant files include:

- `DBManager.Abstractions/Config/IPolicyConfigurationStore.cs`;
- `DBManager.Abstractions/Config/ConfigCommands.cs`;
- `DBManager.Abstractions/Config/ConfigQueryResults.cs`;
- `DBManager.Postgres/Config/ConfigEntities.cs`;
- `DBManager.Postgres/Config/PolicyConfigurationStore.cs`.

Existing entities include:

- policy profiles;
- immutable policy revisions;
- policy promotion events;
- calibration artifacts;
- policy/artifact links;
- deployments;
- deployment activation events;
- deployment assignments.

Do not create a parallel file-based deployment design. Extend and wire these stores.

## 2.8 The existing deployment schema is too restrictive for the target workflow

`DeploymentEntity` currently contains one `PolicyRevisionId`, while `DeploymentAssignmentEntity` contains only an instrument and strategy ID.

The target system must allow one broker-account runtime to host multiple independently revisioned agent instances, for example:

```text
EUR/USD → Structural Sweep profile revision 17 → Automatic
GBP/JPY → Structural Pullback profile revision 9 → Shadow
EUR/JPY → Improved Progressive profile revision 12 → ManualApproval
```

Therefore, the exact policy revision must be attached to the deployment-agent assignment, not only to the account-level deployment row.

## 2.9 The simulator/experiment overhaul and PostgreSQL/reporting work are companion requirements

Retain and implement the requirements from the existing local blueprints:

- `TradingHub_Structural_Confluence_Agent_and_Simulator_Experiment_Blueprint_v2.md`;
- `TradingHub_PostgreSQL_Runtime_Migration_and_Structured_Reporting_Blueprint.md`.

This document is the next integration layer. Where a conflict exists, prefer the stronger safety, immutability, leakage prevention, and source-neutrality rule.

---

# 3. Non-negotiable invariants

1. **One Agent implementation across environments.** Simulator and live must not have separate strategy implementations.
2. **Agent source neutrality.** Agents consume normalized snapshots and never know where data originated.
3. **No broker writes from Agent code.** Only the live execution gateway may write to a broker.
4. **One canonical resolved package.** Simulator, parity tests, live shadow, Demo, and later live execution consume the same immutable package.
5. **Exact revisions only.** Never load “latest approved” implicitly for a running deployment. Resolve and pin exact IDs.
6. **No live-only behavioural overrides.** Live deployment may choose account, instruments, mode, scheduling, and safety permission; it may not silently change strategy, indicators, risk, portfolio, or management rules.
7. **No automatic real-money promotion.** Automation may create candidates and may auto-deploy to shadow when configured. Demo/live execution requires explicit permission and audit records.
8. **One executable owner per account/instrument.** Many shadow agents may observe the same instrument, but only one manual/automatic agent may own executable entry authority for that account/instrument.
9. **Open positions stay managed.** Stopping or replacing an entry agent must not orphan positions. Position management remains pinned to the entry package until the position is flat.
10. **Decision-epoch atomicity.** Dynamic registration, removal, or hot-swap becomes visible only between decision epochs.
11. **Fail closed for new live entries.** Persistence uncertainty, lease loss, reconciliation ambiguity, unknown submission certainty, missing artifacts, hash mismatch, or capability mismatch pauses new entries.
12. **Emergency management remains possible.** Reporting failures must not prevent risk reduction, protection, or emergency close operations.
13. **PostgreSQL is authoritative.** Profiles, revisions, permissions, deployments, assignments, state, and audit events are database-backed.
14. **Secrets are referenced, not stored as ordinary JSON/text.** Browser APIs never return secret material.
15. **No database calls in candle/indicator/annotation hot paths.** Resolve runtime packages before activation and use immutable in-memory objects.
16. **No per-candle operational logging.** Persist meaningful lifecycle transitions and aggregated inactivity windows.
17. **Simulation learning is leakage-safe.** Training warm-up, learning window, embargo, evaluation warm-up, and held-out evaluation are explicit and immutable.
18. **Plug-and-play does not mean unsafe.** The UI may simplify operation but must not bypass validation, permission, reconciliation, or approval gates.

---

# 4. Target architecture

```text
                       PostgreSQL
                           │
       ┌───────────────────┼────────────────────┐
       │                   │                    │
 Agent/Policy Library   Deployment Store   Reports/Audit
       │                   │                    │
       └──────────┬────────┴──────────┬─────────┘
                  │                   │
         AgentPackageResolver   DeploymentOrchestrator
                  │                   │
         ResolvedAgentPackage         │
                  │                   │
     ┌────────────┼────────────┐      │
     │            │            │      │
 Simulator   Parity Runner   Live Runtime Registry
     │                         │
 Historical/Replay Adapter     ├── Live market-data adapter
                               ├── Shared analysis registry
                               ├── Agent supervisor
                               ├── Portfolio coordinator
                               └── Execution gateway
```

The central portable object is an immutable `ResolvedAgentPackage`.

The Agent sees only an `AgentMarketContext` created by an environment adapter. Activation mode and broker capabilities remain outside the Agent.

---

# 5. Define a generic Agent catalogue

## 5.1 Add environment-neutral agent identifiers

Create a shared location in `Agent`, for example:

```text
Agent/Catalog/AgentTypeIds.cs
```

with stable canonical IDs:

```csharp
public static class AgentTypeIds
{
    public const string LegacyProgressive = "legacy-progressive";
    public const string ImprovedProgressive = "improved-progressive";
    public const string StructuralConfluence = "structural-confluence";
}
```

Preserve aliases such as `legacy` and `improved` only in request parsers. Persist canonical IDs.

## 5.2 Add a generic serialized Agent definition

Introduce a versioned discriminated model in `Agent` or `TradingPolicies`:

```csharp
public sealed record AgentDefinition
{
    public required string AgentTypeId { get; init; }
    public required int SchemaVersion { get; init; }
    public required JsonElement Options { get; init; }
}
```

Prefer a safe serializer with explicit registered types rather than runtime reflection or unrestricted polymorphic type names.

Provide typed helpers:

```csharp
AgentDefinition FromProgressive(...)
AgentDefinition FromStructuralConfluence(...)
ProgressiveStrategyOptions ReadProgressiveOptions()
StructuralConfluenceOptions ReadStructuralOptions()
```

Backward compatibility:

- existing `TradingPolicyProfile` JSON containing `AgentKind`/`AgentOptions` must still load;
- migration must convert it into canonical `AgentDefinition` in memory;
- newly written revisions use only the new representation after the migration gate.

## 5.3 Add an extensible builder contract

Create:

```csharp
public interface ITradingAgentBuilder
{
    string AgentTypeId { get; }
    AgentDescriptor Describe();
    ITradingAgent Build(AgentDefinition definition);
}
```

`AgentDescriptor` should expose:

- canonical ID;
- display name;
- description;
- definition schema version;
- required feature capabilities;
- supported exit-management model;
- supported deployment modes;
- whether calibration/meta-model/management calibration are supported;
- default intervals or an interval resolver;
- whether automatic Demo or live operation has been certified.

Register one builder per type using DI. Do not add a new central switch for every future Agent.

## 5.4 Add one catalogue/factory

Create:

```csharp
public interface ITradingAgentCatalog
{
    IReadOnlyList<AgentDescriptor> List();
    AgentDescriptor Get(string agentTypeId);
    ITradingAgent Create(AgentDefinition definition);
}
```

Implement it using the registered `ITradingAgentBuilder` collection and reject:

- duplicate IDs;
- unknown IDs;
- unsupported schema versions;
- invalid options;
- incompatible feature policies.

## 5.5 Migrate existing construction sites

Replace hard-coded strategy switches in:

- `Simulator/Services/BacktestApplicationService.cs`;
- `Simulator/Models/BacktestConfiguration.cs`;
- `BacktestRunner/Program.cs`;
- `QuantResearchRunner/*`;
- `LiveTrading/Agents/LiveAgentFactory.cs`;
- policy promotion code;
- Dashboard validation/API code.

`LiveAgentFactory` should become a thin environment adapter over the shared catalogue and decision-pipeline factory, not a progressive-only strategy constructor.

---

# 6. Define the canonical immutable `ResolvedAgentPackage`

Create an environment-neutral package model, preferably in `TradingPolicies`:

```csharp
public sealed record ResolvedAgentPackage
{
    public required Guid PolicyId { get; init; }
    public required Guid PolicyRevisionId { get; init; }
    public required int Revision { get; init; }

    public required string StrategyId { get; init; }
    public required string StrategyVersion { get; init; }
    public required AgentDefinition AgentDefinition { get; init; }

    public required RuntimeFeaturePolicy FeaturePolicy { get; init; }
    public required PositionSizingOptions PositionSizing { get; init; }
    public required AdaptiveRiskOptions AdaptiveRisk { get; init; }
    public required PortfolioRiskOptions PortfolioRisk { get; init; }
    public required CorrelationRiskOptions CorrelationRisk { get; init; }
    public required TradingConditionOptions TradingConditions { get; init; }
    public required TradingSafetyOptions AccountSafety { get; init; }
    public required PositionManagementOptions PositionManagement { get; init; }
    public required RegimeManagementOptions RegimeManagement { get; init; }

    public Guid? SetupCalibrationArtifactId { get; init; }
    public Guid? MetaModelArtifactId { get; init; }
    public Guid? ManagementCalibrationArtifactId { get; init; }

    public SetupCalibrationArtifact? SetupCalibration { get; init; }
    public ISetupMetaModel? MetaModel { get; init; }
    public TradeManagementCalibration? ManagementCalibration { get; init; }

    public required IReadOnlySet<BarInterval> RequiredIntervals { get; init; }
    public required string AgentDefinitionHash { get; init; }
    public required string FeatureSchemaHash { get; init; }
    public required string AnnotationProfileHash { get; init; }
    public required string ManagementPolicyHash { get; init; }
    public required string ConfigurationHash { get; init; }

    public required string SourceCommit { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

Names may be adapted, but the semantics must remain.

## 6.1 One package resolver

Create:

```csharp
public interface IAgentPackageResolver
{
    Task<ResolvedAgentPackage> ResolveAsync(
        Guid policyRevisionId,
        CancellationToken cancellationToken);
}
```

The resolver must:

1. load the exact immutable policy revision;
2. deserialize and validate the Agent definition;
3. load exactly linked artifact IDs;
4. validate strategy/version/schema compatibility;
5. verify all hashes;
6. build calibration/meta-model objects;
7. resolve required intervals;
8. return one immutable object.

Cache resolved packages by:

```text
PolicyRevisionId + ConfigurationHash + artifact content hashes
```

with bounded memory and explicit invalidation only when immutable records are retired or storage becomes unavailable. Since revisions are immutable, active package objects do not change in place.

## 6.2 Environment-specific data must not enter the package

Exclude:

- broker account ID;
- broker credentials;
- REST/streaming URL;
- Demo/live mode;
- historical source;
- cache directory;
- output directory;
- host URL;
- deployment activation mode;
- current balance/positions;
- operator identity.

Those belong to deployment or adapters.

---

# 7. Preserve data-source neutrality

## 7.1 Keep the Agent contract free of source metadata

The simulator and live host must both produce the same normalized inputs:

- completed candle snapshots;
- chart-analysis snapshots;
- account view;
- owned position/order view;
- spread and quote quality where required;
- market-data quality state;
- market time and decision epoch.

The Agent must not branch on source identity.

## 7.2 Add explicit source adapters outside Agent

Define a boundary such as:

```csharp
public interface IAgentContextSource
{
    IAsyncEnumerable<AgentEvaluationInput> ReadAsync(
        AgentInputSubscription subscription,
        CancellationToken cancellationToken);
}
```

or retain current engine-specific loops if introducing this interface would produce unnecessary churn. The required invariant is more important than the exact name.

Adapters:

- simulator historical/replay source;
- recorded-live parity source;
- OANDA live source.

All adapters must normalize:

- UTC timestamps;
- half-open candle intervals;
- completed-candle semantics;
- instrument keys;
- price precision;
- quote freshness;
- data-quality flags.

## 7.3 Keep activation mode external

Continue using deployment-level activation modes such as:

```text
ObserveOnly
Shadow
ManualApproval
Automatic
```

Never pass this value into strategy logic. It controls what the host does with an immutable Agent decision.

---

# 8. Automate simulator experiments and candidate selection

Implement the experiment overhaul described in the Structural Confluence/Simulator blueprint and connect it to the package lifecycle.

## 8.1 Explicit leakage-safe timeline

Every experiment profile must resolve:

```text
Training analysis warm-up
→ ML/calibration learning window
→ embargo gap
→ evaluation analysis warm-up
→ held-out evaluation window
```

Example:

```text
Learning window:           2026-04-01T00:00Z → 2026-05-21T00:00Z
External embargo:          2026-05-21T00:00Z → 2026-06-01T00:00Z
Held-out evaluation:       2026-06-01T00:00Z → 2026-07-01T00:00Z
```

Calculate warm-up from the slowest required interval and the maximum lookback/confirmation requirement of:

- indicators;
- swings;
- price-action calibration;
- NeoWave;
- supply/demand;
- liquidity;
- regime;
- management intervals.

Reject a plan whose supplied data cannot satisfy the calculated warm-up.

## 8.2 First-class profile matrix

Support profile templates and generated variants:

```text
Sweep Base
Sweep + CCI Soft
Sweep + CCI Required
Sweep + Supply/Demand Preferred
Sweep + CCI + Supply/Demand
Pullback Base
Break/Retest + CCI
```

Each resolved profile gets:

- unique profile ID;
- immutable revision;
- complete package draft;
- deterministic configuration hash;
- independent training artifacts;
- independent Agent/portfolio/management state.

## 8.3 Parallelism

Profiles sharing compatible candle and annotation requirements must reuse:

- candle acquisition/cache;
- immutable frames;
- compatible annotation snapshots.

Run profile workers in bounded parallelism controlled by one resource governor. Prevent nested unbounded `Task.Run` usage and oversubscription.

## 8.4 Automated ranking

Add a configurable stability score, not a maximum-profit selector.

A candidate must satisfy hard constraints before ranking, for example:

- minimum held-out trade count;
- positive net expectancy after costs;
- maximum drawdown cap;
- acceptable tail loss;
- acceptable Brier/calibration quality;
- minimum walk-forward pass rate;
- sensitivity stability around neighbouring parameters;
- no single instrument or short period dominating results;
- data-quality status not `Invalid`;
- no leakage or warm-up violation.

Then rank eligible profiles using a documented score combining:

- held-out expectancy;
- drawdown-adjusted return;
- walk-forward consistency;
- Monte Carlo survival;
- calibration reliability;
- parameter sensitivity;
- cross-instrument/regime stability.

Persist the score breakdown. Do not hide the formula in UI code.

## 8.5 Automatic shortlist and promotion candidate

At experiment completion:

1. generate a comparison report;
2. mark profiles as pass/fail against hard gates;
3. automatically shortlist top stable candidates;
4. optionally create a `PromotionCandidate` record;
5. require explicit operator approval before executable Demo/live permission.

Automation may create and deploy a candidate to **Shadow** when an operator-configured rule allows it. It may not automatically authorize real-money execution.

---

# 9. Policy lifecycle and permissions

## 9.1 Separate validation status from environment permission

The current statuses (`Research`, `Reviewed`, `ApprovedForDemo`, `Retired`) are too coarse.

Introduce a backward-compatible lifecycle projection, for example:

```text
Draft/Research
Backtested
Validated
ShadowCertified
DemoCertified
LiveCertified
Suspended
Retired
```

Separately store allowed execution scopes:

```csharp
[Flags]
public enum PolicyExecutionPermission
{
    None = 0,
    Shadow = 1,
    DemoManual = 2,
    DemoAutomatic = 4,
    LiveManual = 8,
    LiveAutomatic = 16
}
```

The exact enum names may differ, but permissions must be explicit, append-only/audited, and account-environment aware.

Do not infer permission from a display label or from the presence of a model artifact.

## 9.2 Promotion gates

Suggested automated/manual gates:

```text
Experiment passes hard gates
    → eligible for Reviewed/Validated

Decision-parity certification passes
    → eligible for Shadow

Live-shadow minimum sample/duration and drift checks pass
    → eligible for Demo review

Demo execution certification passes
    → eligible for Live review

Explicit operator approval
    → permission granted
```

Automatic transitions may advance evidence/certification states, but Demo/live permissions require a durable approval event.

## 9.3 Champion/challenger

Allow:

- one active champion package with decision influence;
- one or more challenger packages in shadow;
- same market observations shared between them;
- independent state and outcomes;
- comparison reports.

Never auto-replace the champion in live execution merely because a challenger has a better short-term result.

---

# 10. Add decision-parity certification

## 10.1 Recorded input package

Create a deterministic recording format containing:

- completed candles;
- bid/ask quotes where available;
- account snapshots;
- positions/orders;
- data-quality state;
- exact resolved Agent package manifest;
- decision epochs.

## 10.2 Parity runner

Add a tool/service, for example:

```text
TradingCore.Parity or Simulator/Parity
```

that feeds the same recording into:

- simulator decision runtime;
- live-shadow decision runtime.

Compare per decision epoch:

- setup/playbook state transitions;
- action;
- confidence;
- reason codes;
- stop/target geometry;
- risk multiplier;
- setup-calibration result;
- meta-model result;
- management recommendation;
- normalized candidate identity fields.

Allowed differences must be explicitly listed, such as host timestamp or transport diagnostics. Strategy differences are failures.

## 10.3 Persist certification

Store:

- package revision;
- source commit;
- recording hash;
- simulator build hash;
- live build hash;
- compared epoch count;
- mismatch count;
- mismatch details;
- certification status;
- certified at/by.

The deployment preflight must reject a package requiring parity certification when its certification is missing, stale, or tied to a different configuration hash.

---

# 11. Extend PostgreSQL for multi-agent deployments

Use migrations; do not rewrite existing migration history.

## 11.1 Treat deployment as an account runtime/session

`config.deployments` should represent one broker-account deployment/runtime session, not one policy revision.

Recommended fields/statuses:

```text
DeploymentId
BrokerAccountId
HostInstanceId
Environment
Status
RequestedAt
PreparingAt
WarmingUpAt
RunningAt
PausedAt
DrainingAt
StoppedAt
FaultedAt
RequestedBy
StopReason
DeploymentHash
ConcurrencyToken
```

Lifecycle:

```text
Requested
→ Validating
→ Preparing
→ WarmingUp
→ Reconciling
→ Running
↔ Paused
→ Draining
→ Stopped
or Faulted
```

Keep a compatibility projection for old `Active/Stopped` consumers during migration.

## 11.2 Add a deployment-agent entity

Replace/extend the current `DeploymentAssignmentEntity` with an entity similar to:

```csharp
public sealed class DeploymentAgentEntity
{
    public Guid DeploymentAgentId { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public long InstrumentId { get; set; }
    public string StrategyId { get; set; }
    public AgentMode AgentMode { get; set; }
    public DeploymentAgentStatus Status { get; set; }
    public bool Enabled { get; set; }
    public string PackageHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
}
```

A deployment may contain multiple agent rows with different policy revisions.

Enforce:

- unique `(DeploymentId, InstrumentId, PolicyRevisionId)` or another deterministic uniqueness rule;
- at most one executable/manual-or-automatic owner per active account/instrument;
- many shadow/observe agents allowed;
- exact package hash pinned.

Use a PostgreSQL exclusion/partial unique index or transaction/advisory-lock validation for executable ownership.

## 11.3 Add durable deployment commands

Create a command queue/table, for example:

```text
operations.deployment_commands
```

Fields:

```text
CommandId
DeploymentId
DeploymentAgentId nullable
CommandType
RequestedAt
RequestedBy
ExpectedVersion
PayloadJson
Status
ClaimedByHost
ClaimedAt
CompletedAt
ErrorCode
ErrorMessage
IdempotencyKey
```

Commands include:

```text
CreateDeployment
StartAgent
PauseAgent
ResumeAgent
StopAgent
DrainAgent
ReplaceAgentRevision
StopDeployment
Reconcile
```

Commands must be idempotent and claimed with `FOR UPDATE SKIP LOCKED` or an equivalent safe mechanism.

## 11.4 Append-only deployment events

Expand deployment activation events or add:

```text
operations.deployment_events
```

for meaningful lifecycle transitions:

- validation started/passed/failed;
- package resolved;
- lease acquired/lost;
- market subscription started;
- analysis warm-up started/completed;
- reconciliation completed/failed;
- Agent registered;
- mode changed;
- Agent draining;
- Agent stopped;
- deployment faulted/recovered.

No per-candle events.

## 11.5 Extend configuration stores

`IPolicyConfigurationStore` currently provides exact revision loading and one active deployment query. Add query/write contracts needed by the UI and orchestrator:

```csharp
Task<Page<PolicySummary>> ListPoliciesAsync(...)
Task<Page<PolicyRevisionSummary>> ListPolicyRevisionsAsync(...)
Task<PolicyPermissionDetail> GetPermissionsAsync(...)
Task<DurableResult> GrantPermissionAsync(...)
Task<DeploymentDetail?> GetDeploymentAsync(...)
Task<Page<DeploymentSummary>> ListDeploymentsAsync(...)
Task<IReadOnlyList<DeploymentAgentDetail>> ListDeploymentAgentsAsync(...)
```

Prefer focused read stores if the interface becomes too broad.

## 11.6 Broker/reference resolution

Use the PostgreSQL broker/reference migration from the companion database blueprint:

- broker environment and endpoint revisions;
- account records;
- secret references;
- instrument catalogue/mappings;
- broker capability certification.

A deployment references exact broker/account/instrument revisions. Secret material is resolved server-side.

---

# 12. Implement dynamic live deployment orchestration

## 12.1 Replace static startup registration

Refactor `LiveTradingHost/LiveEngineHostedService.cs` so it no longer treats `LiveMarkets` and `LivePolicyBundles` as the authoritative runtime assignment source.

During migration:

- retain static configuration as an optional bootstrap/import path;
- convert static entries into database deployment requests;
- log a deprecation warning;
- do not maintain two independent authoritative paths.

Create services such as:

```csharp
public interface ILiveDeploymentOrchestrator
{
    Task<DeploymentPreflightResult> ValidateAsync(...);
    Task<Guid> RequestStartAsync(...);
    Task RequestPauseAsync(...);
    Task RequestResumeAsync(...);
    Task RequestStopAsync(...);
    Task RequestDrainAsync(...);
}
```

and an internal background command processor.

## 12.2 Runtime registries

Introduce explicit dynamic registries:

```text
BrokerAccountRuntimeRegistry
MarketSubscriptionRegistry
AnalysisRuntimeRegistry
AgentRuntimeRegistry
ExecutionOwnershipRegistry
PositionManagementRegistry
```

Names may vary.

### `MarketSubscriptionRegistry`

- lazily starts broker streams/subscriptions per account/instrument;
- reference-counts consumers;
- prevents duplicate streams where broker semantics allow sharing;
- stops a subscription only when no active analysis/position-management consumer requires it.

### `AnalysisRuntimeRegistry`

- keys analysis by instrument plus complete annotation-profile hash;
- shares compatible analysis snapshots among agents;
- maintains reference counts;
- performs warm-up before exposing the profile as ready;
- never shares mutable Agent state.

### `AgentRuntimeRegistry`

- dynamically registers/unregisters `LiveAgentRuntime` instances;
- uses exact `ResolvedAgentPackage`;
- applies changes only at a decision-epoch boundary;
- exposes readiness, warm-up, last decision, and fault state.

### `ExecutionOwnershipRegistry`

- atomically guarantees one executable owner per broker account/instrument;
- shadow agents never acquire executable ownership;
- releases ownership only after safe drain/stop rules complete.

### `PositionManagementRegistry`

- pins each open position to the entry package revision;
- remains active after the entry Agent is disabled;
- releases package/analysis dependencies only after the position is flat and reconciled.

## 12.3 Start workflow

A single “Start configured Agent” operation must perform:

1. accept an idempotent deployment request;
2. load broker account/environment and exact instrument mappings;
3. load exact policy revision;
4. resolve the immutable Agent package and artifacts;
5. verify package/configuration hashes;
6. verify policy permission for requested mode/environment;
7. verify parity certification requirements;
8. verify broker capabilities required by exit/management policy;
9. verify global broker-write and automatic-execution switches;
10. acquire/verify account lease;
11. reconcile broker account/orders/positions;
12. reserve executable ownership when applicable;
13. create/reuse market subscription;
14. create/reuse analysis profile;
15. perform required warm-up;
16. build shared Agent + decision pipeline through the canonical factory;
17. register at a decision-epoch boundary;
18. persist `Running` state and activation event;
19. publish Dashboard status.

Any failure must:

- produce a structured error code;
- release partially acquired resources;
- preserve existing active agents and broker-side stops;
- leave the new deployment-agent row `Faulted` or `ValidationFailed`;
- never partially enable broker writes.

## 12.4 Stop/drain workflow

`Stop Agent` must not mean “dispose everything immediately.”

Support:

### Stop new entries

- remove the Agent from future candidate generation at the next epoch;
- keep management for owned open positions;
- keep required market/analysis streams;
- mark `Draining` until flat.

### Immediate disable for shadow

- stop paper candidate generation;
- close or preserve paper positions according to an explicit operator option;
- release resources when no longer needed.

### Full deployment stop

- pause new entries;
- reconcile;
- require explicit decision for existing positions (`KeepManaging`, `Flatten`, or reject stop while positions exist`);
- persist the choice;
- never silently orphan a real position.

## 12.5 Package replacement/hot-swap

A new policy revision is a new immutable package.

Replacement flow:

1. resolve and validate challenger package;
2. warm required analysis;
3. optionally run shadow overlap;
4. switch entry authority at a decision-epoch boundary;
5. existing positions remain pinned to old management revision;
6. new positions use the new revision;
7. persist activation event and both hashes.

Do not mutate options inside a running Agent instance.

## 12.6 Crash recovery

On host restart:

1. acquire account lease;
2. load active/preparing/draining deployments assigned to the host/account;
3. reconcile authoritative broker state;
4. resolve exact packages;
5. recover checkpoints/events;
6. rebuild required streams/analysis;
7. restore Agent state where supported;
8. restore position-management state;
9. resume only if all safety checks pass;
10. otherwise remain paused and explain the fault in the Dashboard.

Never substitute another “latest” policy revision during recovery.

---

# 13. Plug-and-play Dashboard experience

Split the current large live component into focused components. Suggested structure:

```text
Dashboard/src/components/live/
    AgentLibraryPanel.vue
    AgentProfileDetail.vue
    DeploymentWizard.vue
    DeploymentPreflight.vue
    ActiveDeploymentsPanel.vue
    LiveAgentsTable.vue
    LiveSafetyPanel.vue
    LiveCandidatesPanel.vue
    LivePositionsPanel.vue
    LiveReportsPanel.vue
```

Keep `LiveDemoPanel.vue` as a composition shell or retire it after migration.

## 13.1 Agent Library

Display configured immutable profiles with:

- display name;
- canonical Agent type;
- strategy/version;
- profile revision;
- configuration hash prefix;
- enabled playbooks;
- required intervals;
- linked calibration/meta-model artifacts;
- held-out metrics;
- walk-forward/Monte Carlo status;
- parity certification;
- shadow/demo/live permissions;
- current deployment state;
- retired/suspended warnings.

Filters:

- Agent type;
- lifecycle/certification;
- permission;
- instrument group;
- source experiment;
- currently deployed.

The UI must not allow editing an immutable revision. “Edit” creates a new draft revision.

## 13.2 Deployment wizard

Steps:

1. **Select account/environment**
2. **Select exact configured Agent revision**
3. **Select compatible instruments**
4. **Select activation mode**
5. **Review preflight and safety gates**
6. **Start**

Deployment-only choices may include:

- instrument assignment;
- activation mode;
- deployment schedule/session enablement;
- operator note;
- optional deployment-level maximum risk cap that can only reduce package risk, never increase it;
- whether a shadow run should create paper outcomes.

Do not expose duplicate strategy/indicator/management settings in the live wizard.

## 13.3 Preflight response

Show explicit checks:

```text
Package hash valid
Artifacts present and compatible
Policy permission allows OANDA Demo Automatic
Decision parity current
Broker account lease available
Instrument mapping available
Required analysis intervals supported
Warm-up data available
Broker supports required protective stop
One executable owner rule satisfied
Database persistence healthy
Reconciliation clean
Global broker writes enabled
Automatic execution enabled
```

The Start button remains disabled if any mandatory gate fails.

## 13.4 Active deployments

Display parent deployment and child Agent instances:

- status;
- account/environment;
- instrument;
- mode;
- exact policy revision/hash;
- warm-up progress;
- last decision epoch;
- candidates/trades;
- model acceptance rate;
- data quality;
- drift state;
- open positions pinned to package;
- fault/safety message.

Controls:

- pause entries;
- resume after reconciliation;
- stop/drain Agent;
- stop deployment;
- reconcile;
- inspect report;
- deploy challenger in shadow;
- replace revision after preflight.

Dangerous actions require confirmation, operator identity, control token/authorization, and audit event.

## 13.5 Auto-refresh

Use SignalR for deployment-state changes and commands. Polling fallback may remain. Do not stream large package JSON or secrets through SignalR.

---

# 14. API design

Place management APIs in `LiveTradingHost` because it owns runtime state. `DashboardLive` may proxy them but must not create a second orchestration implementation.

Suggested endpoints:

## Agent catalogue and policy library

```http
GET /api/live/agent-types
GET /api/live/policies
GET /api/live/policies/{policyRevisionId}
GET /api/live/policies/{policyRevisionId}/certification
GET /api/live/policies/{policyRevisionId}/permissions
```

## Deployment validation and lifecycle

```http
POST /api/live/deployments/preflight
POST /api/live/deployments
GET  /api/live/deployments
GET  /api/live/deployments/{deploymentId}
POST /api/live/deployments/{deploymentId}/pause
POST /api/live/deployments/{deploymentId}/resume
POST /api/live/deployments/{deploymentId}/reconcile
POST /api/live/deployments/{deploymentId}/stop
```

## Agent assignment lifecycle

```http
POST /api/live/deployments/{deploymentId}/agents
POST /api/live/deployment-agents/{deploymentAgentId}/pause
POST /api/live/deployment-agents/{deploymentAgentId}/resume
POST /api/live/deployment-agents/{deploymentAgentId}/drain
POST /api/live/deployment-agents/{deploymentAgentId}/stop
POST /api/live/deployment-agents/{deploymentAgentId}/replace
```

## Promotion/certification

These may remain in `DashboardLive`/research APIs if that service owns research, but use shared stores:

```http
POST /api/policies/{policyRevisionId}/request-parity
POST /api/policies/{policyRevisionId}/grant-permission
POST /api/policies/{policyRevisionId}/suspend
POST /api/policies/{policyRevisionId}/retire
```

## Requirements

- idempotency key on mutation requests;
- optimistic concurrency/version on deployment commands;
- structured validation errors;
- exact IDs in responses;
- no secret material;
- authorization/control-token policy at least as strong as existing live control endpoints;
- audit actor/reason for every mutation.

---

# 15. CLI parity

Add CLI commands over the same application services, not separate logic.

Examples:

```bash
dotnet run --project BacktestRunner -- experiment run --plan experiment.json

dotnet run --project QuantResearchRunner -- profile shortlist --experiment <id>

dotnet run --project QuantResearchRunner -- profile promote --run <id> --profile <id>

dotnet run --project LiveTradingHost -- policy list --status validated

dotnet run --project LiveTradingHost -- deployment preflight \
  --account <id> --policy-revision <id> --instrument FX:EUR/USD --mode shadow

dotnet run --project LiveTradingHost -- deployment start \
  --account <id> --policy-revision <id> --instrument FX:EUR/USD --mode shadow

dotnet run --project LiveTradingHost -- deployment status --id <deploymentId>

dotnet run --project LiveTradingHost -- deployment drain-agent --id <deploymentAgentId>
```

If command hosting inside `LiveTradingHost` complicates normal web startup, create a thin `TradingHub.AdminCli` project that calls shared application services.

Do not duplicate validation or package resolution in the CLI.

---

# 16. Automation policies

## 16.1 Safe automation that should be supported

Configurable automation may:

- generate simulator profile matrices;
- run bounded profiles in parallel;
- calculate training/warm-up/embargo/evaluation windows;
- train and freeze artifacts;
- rank eligible profiles;
- create promotion candidates;
- schedule/execute decision-parity certification;
- auto-deploy a validated candidate to live **Shadow**;
- create paper outcomes;
- detect drift or operational safety faults;
- pause new entries;
- create a challenger after retraining;
- produce reports and alerts.

## 16.2 Automation that must remain prohibited by default

Do not automatically:

- grant real-account permissions;
- increase risk;
- select a “latest” model for an active deployment;
- replace an executable champion based only on short-term performance;
- activate a model trained on the current evaluation/live outcomes without validation;
- resume after reconciliation or persistence ambiguity unless all configured recovery gates pass;
- flatten real positions without explicit operator command or a separately approved emergency policy.

## 16.3 Optional auto-shadow workflow

Add an operator-configurable rule:

```text
When an experiment candidate:
- passes all hard gates,
- passes parity,
- has Shadow permission,
- targets an approved list of instruments/accounts,
then create a Shadow deployment request automatically.
```

Persist the automation rule revision and why it fired.

---

# 17. Integrate structured reporting and logging

Use the companion PostgreSQL/reporting blueprint. This task must add lifecycle events needed for plug-and-play operations.

## 17.1 Meaningful deployment/Agent events

Persist events such as:

- package selected;
- preflight passed/failed;
- Agent warm-up started/completed;
- Agent registered/paused/resumed/draining/stopped;
- decision parity certified/failed;
- permission granted/revoked;
- challenger activated in shadow;
- executable ownership acquired/released;
- model drift warning;
- safety pause;
- reconciliation mismatch;
- package replacement;
- position remains pinned to retired entry revision.

Do not write one event per candle or ordinary `Observe` decision.

## 17.2 Agent report

For each deployment Agent, report:

- exact package and source experiment;
- learning/embargo/evaluation windows;
- model/artifact IDs;
- market/analysis warm-up;
- setup lifecycle counts;
- candidates produced;
- rejected by strategy/calibration/meta-model/risk/portfolio/execution;
- paper or actual outcomes;
- MFE/MAE;
- management actions;
- data-quality incidents;
- drift;
- configuration parity status;
- deployment lifecycle/faults.

## 17.3 Correlation IDs

Carry:

```text
ExperimentId
SimulationId
PolicyRevisionId
DeploymentId
DeploymentAgentId
AgentInstanceId
DecisionEpochId
CandidateId
TradeId
OrderId
InstrumentId
ModelArtifactId
```

through events and reports.

---

# 18. Runtime behaviour and parity rules

## 18.1 Same decision, different execution adapter

Given identical normalized inputs and package hashes, simulator and live shadow must produce the same Agent-level decision.

Execution may differ because live brokers introduce:

- current spread;
- latency;
- rejection;
- slippage;
- partial fills;
- gaps;
- financing;
- account exposure.

Reports must separate:

```text
Decision parity
Execution deviation
Outcome deviation
```

## 18.2 Quote-level management

Where live management uses quotes but historical simulation has only OHLC candles, clearly record fidelity limits and use deterministic intrabar assumptions. Support recorded quote replay for stronger parity tests.

## 18.3 Deployment-level risk cap

The deployment may apply a cap multiplier `<= 1` for emergency/rollout purposes. It must never increase the immutable package’s risk. Persist the cap and its reason as deployment metadata.

---

# 19. Security and safety

1. Continue requiring the existing live-control authorization/token for mutations.
2. Add role/permission checks if identity integration exists; otherwise preserve operator name plus control token and design interfaces for later authorization.
3. Secret references resolve only inside server processes.
4. Return masked account identifiers in the UI where appropriate.
5. Explicitly display `Demo` versus real environment throughout preflight and active deployment views.
6. Require stronger confirmation for `Automatic` and any real account.
7. Preserve global kill switches:
   - broker writes enabled;
   - automatic execution enabled;
   - pause new entries;
   - cancel pending;
   - flatten.
8. Keep mandatory broker-side protection checks.
9. Require clean reconciliation before start/resume.
10. Reject an Agent package whose required capability has not been certified for the selected broker/environment.
11. Never allow a Shadow Agent to reach the live execution coordinator.
12. Ensure command retries cannot duplicate an Agent registration or broker order.

---

# 20. Failure handling

## Database unavailable before start

- reject/prevent new deployment start;
- keep existing runtime behaviour according to critical-persistence rules;
- show explicit health failure.

## Database fails during active live operation

- pause new entries when critical state cannot be durably persisted;
- continue broker-event processing and protection/exit management where safely possible;
- buffer non-critical reporting events within bounded memory/disk policy;
- never guess deployment state.

## Warm-up failure

- mark deployment Agent faulted;
- do not register it;
- leave other agents unaffected;
- release unused resources.

## Market stream failure

- mark dependent Agents degraded/paused;
- preserve position management using available broker protection;
- reconnect with bounded backoff;
- reconcile before resuming.

## Lease loss

- immediately stop new executable entries;
- retain safe read/reconciliation behaviour;
- avoid two writers;
- surface a critical event.

## Host crash during command

- command remains idempotently recoverable;
- on restart, inspect command/deployment state and broker truth;
- complete or compensate safely;
- never create duplicate runtime instances.

---

# 21. Code-specific change map

## `Agent`

- add canonical Agent IDs;
- add `AgentDefinition` and safe serializer;
- add `AgentDescriptor`, `ITradingAgentBuilder`, catalogue;
- register progressive and Structural Confluence builders;
- keep `ITradingAgent` environment-neutral.

## `TradingPolicies`

- migrate `TradingPolicyProfile` to generic `AgentDefinition`;
- preserve legacy profile deserialization;
- add `ResolvedAgentPackage`;
- add package hash/manifest utilities;
- add execution permissions/certification models.

## `Simulator`

- replace hard-coded strategy switches with catalogue/package resolver;
- accept exact package/profile revisions;
- complete experiment orchestration;
- persist immutable run manifests;
- emit promotion candidates;
- correctly enforce shadow/executable shared-portfolio semantics.

## `BacktestRunner`

- use shared experiment/profile services;
- add batch/experiment commands;
- no local strategy switches.

## `QuantResearch` / `QuantResearch.Training` / `QuantResearchRunner`

- train per exact profile revision;
- persist artifact compatibility metadata;
- implement ranking/shortlisting;
- create promotion candidates;
- invoke parity certification workflow.

## `DBManager.Abstractions`

- extend policy list/read and permission contracts;
- add deployment/Agent lifecycle command/read stores;
- add parity certification stores;
- add automation-rule models if implemented in this pass.

## `DBManager.Postgres`

- migrations for generic policy documents as needed;
- permission/certification tables;
- deployment lifecycle expansion;
- deployment-agent rows with exact policy revision;
- durable command queue;
- lifecycle events;
- indexes/constraints/advisory locks;
- query/read models for Dashboard.

## `LiveTrading`

- make live factory generic;
- add dynamic runtime registries;
- add registration/unregistration at epoch boundaries;
- add executable ownership registry;
- add package-pinned position management;
- preserve source-neutral strategy pipeline.

## `LiveTradingHost`

- replace static startup assignment authority with deployment orchestrator;
- command processor;
- package resolver wiring;
- dynamic market/analysis/Agent lifecycle;
- recovery from PostgreSQL;
- management APIs and SignalR events;
- retain static config only as migration/import bootstrap.

## `DashboardLive`

- expose research/promotion APIs where appropriate;
- proxy live management endpoints if needed;
- do not duplicate live orchestration;
- connect simulation results to profile/promotion library.

## `Dashboard`

- Agent Library;
- deployment wizard/preflight;
- active deployments/agents;
- certification and permission display;
- report links;
- refactor current Live Demo component into focused units.

## `TradingJournal`

- do not expand it into a competing persistence system;
- migrate useful concepts to typed database-backed events/report projections;
- retain only compatibility/export utilities if needed.

---

# 22. Testing requirements

## 22.1 Catalogue and profile tests

- every canonical Agent type resolves through the catalogue;
- duplicate builders fail startup;
- unknown/schema-incompatible definitions fail;
- legacy profile JSON migrates deterministically;
- profile hash changes for every behavioural change;
- display-only deployment notes do not change package hash.

## 22.2 Source-neutrality guard tests

Add architecture tests preventing Agent references to:

- `LiveTrading`;
- `LiveTradingHost`;
- simulator source types;
- broker concrete clients;
- DBManager concrete types;
- environment mode flags.

## 22.3 Simulator/experiment tests

- timeline calculation including training warm-up and embargo;
- no evaluation data enters training;
- parallel profiles remain deterministic;
- shared analysis is used only when hashes are compatible;
- ranking hard gates and score breakdown;
- promotion candidate pins exact artifacts and hashes;
- simulator shadow/executable semantics are enforced.

## 22.4 Parity tests

- simulator and live-shadow produce identical decisions for recorded inputs;
- intentional mismatch is detected with useful diagnostics;
- stale certification rejects deployment;
- host-only fields are ignored by comparison;
- management recommendations are compared.

## 22.5 Deployment persistence tests

Using PostgreSQL/Testcontainers:

- multiple shadow agents on one instrument allowed;
- second executable owner rejected atomically;
- exact package revision attached to each deployment Agent;
- idempotent start command;
- concurrent command claims safe;
- lifecycle transition validation;
- crash/retry does not duplicate active deployment;
- permission and environment gates enforced.

## 22.6 Dynamic live runtime tests

- add Agent without host restart;
- warm-up before registration;
- remove at decision-epoch boundary;
- stop entry Agent while position management continues;
- hot-swap new entries while old positions remain pinned;
- resource reference counts correct;
- failure of one Agent does not stop unrelated shadow agents;
- executable ownership released only when safe;
- Shadow candidate cannot reach broker gateway.

## 22.7 API/UI contract tests

- Agent Library pagination/filtering;
- preflight response includes every mandatory gate;
- start disabled on failed gate;
- mutation idempotency;
- no secrets in responses;
- control authorization required;
- SignalR state update after command.

## 22.8 Safety tests

- real account requires explicit real permission;
- `Automatic` requires global switches and capability certification;
- DB/lease/reconciliation failure pauses new entries;
- open positions are never orphaned;
- reporting failure does not block emergency risk reduction;
- no automatic risk increase;
- no implicit latest revision/artifact loading.

## 22.9 Logging tests

- no per-candle DB event;
- lifecycle transitions produce events;
- repeated Observe decisions aggregate;
- correlation IDs link experiment → profile → deployment → candidate → trade;
- secret redaction.

## 22.10 Performance tests

- package resolution occurs before runtime hot path;
- no DB query per candle/decision;
- bounded command/event queues;
- many shadow agents share analysis without excessive rebuilds;
- dynamic registration does not stall decision epochs beyond configured limits.

---

# 23. Implementation phases

Implement in this order. Keep the repository buildable and tests green at each gate.

## Phase 0 — Baseline and inventory

- run full build/tests;
- document current failures separately;
- inventory all hard-coded strategy construction and static live configuration paths;
- record current hashes/sample simulator/live status contracts;
- do not modify behaviour yet.

## Phase 1 — Generic Agent catalogue and package model

- canonical IDs;
- generic Agent definition;
- builders/catalogue;
- backward-compatible policy deserialization;
- `ResolvedAgentPackage` and resolver contract;
- migrate simulator/live factory construction while preserving behaviour.

Acceptance gate:

- legacy/improved produce unchanged decisions on existing tests;
- Structural Confluence can be created through the same catalogue;
- no duplicated strategy switch remains in runtime hosts.

## Phase 2 — PostgreSQL policy/permission/deployment expansion

- extend schema and stores;
- multi-agent exact revision assignments;
- permissions/certifications;
- durable commands/events;
- broker/reference resolution from database;
- migration/import from file/static config.

Acceptance gate:

- exact package can be listed/resolved from PostgreSQL;
- deployment commands are durable/idempotent;
- static config parity import succeeds.

## Phase 3 — Simulator automation and promotion candidate

- experiment timeline/profile matrix/parallel orchestration;
- training/artifact freeze;
- ranking and shortlist;
- immutable profile revision/promotion candidate;
- Dashboard experiment integration.

Acceptance gate:

- one experiment can produce several profiles, train independently, evaluate held-out, and create a reproducible candidate.

## Phase 4 — Decision-parity certification

- recorded input format;
- parity runner;
- persisted certification;
- deployment gate.

Acceptance gate:

- simulator and live-shadow decision paths match for certified packages.

## Phase 5 — Dynamic live deployment runtime

- deployment orchestrator and command processor;
- dynamic registries;
- warm-up/reconciliation/start;
- pause/resume/drain/stop;
- exact package-pinned management;
- crash recovery.

Acceptance gate:

- an approved Shadow Agent can be added and removed without host restart;
- no broker write is possible in Shadow;
- open positions remain managed during drain.

## Phase 6 — Plug-and-play Dashboard and API

- Agent Library;
- deployment wizard/preflight;
- active deployments;
- lifecycle controls;
- SignalR updates;
- CLI parity.

Acceptance gate:

- operator can select a configured approved Agent and start it through the UI without editing files or restarting services.

## Phase 7 — Structured reporting integration

- deployment/Agent lifecycle events;
- candidate/trade explanation links;
- reports and projections;
- no-per-candle guarantee;
- retention/partitioning.

## Phase 8 — Automation and champion/challenger

- auto-shortlist;
- optional auto-shadow;
- drift/safety pause;
- challenger comparisons;
- no automatic executable promotion.

## Phase 9 — Hardening

- failure injection;
- concurrency/load tests;
- database outage/recovery;
- host crash mid-command;
- documentation and migration cleanup;
- deprecate static `LiveMarkets`/`LivePolicyBundles` authority after successful cutover.

---

# 24. Acceptance criteria

The implementation is complete only when all of the following are true:

1. A strategy profile created by simulator/research is stored as one immutable generic Agent package revision.
2. Simulator and live resolve that exact package through one resolver/factory.
3. The Agent has no awareness of source environment.
4. Multiple simulator profiles can be trained/evaluated in parallel with explicit learning/warm-up/embargo/test windows.
5. Stable candidates can be automatically shortlisted and converted to promotion candidates.
6. Decision-parity certification compares simulator and live-shadow outputs.
7. PostgreSQL is the source of truth for profiles, permissions, deployments, assignments, commands, and lifecycle state.
8. The live host can dynamically add/remove an Agent without restart.
9. The Dashboard lists configured agents and supports one guided deployment workflow.
10. The operator cannot change strategy behaviour in the live deployment wizard.
11. One executable owner per account/instrument is enforced atomically.
12. Shadow agents never submit broker orders.
13. Open positions remain managed by their entry package after Agent stop/replacement.
14. Recovery loads exact package revisions and never substitutes “latest.”
15. Demo/live permissions and global execution switches are enforced.
16. Meaningful reports can explain Agent setup, ML, risk, execution, and management without per-candle logging.
17. Existing live safety controls remain functional.
18. All new tests pass and existing tests remain green or documented with justified migration updates.

---

# 25. Explicit non-goals

Do not in this pass:

- make an Agent directly call a broker;
- create separate simulator and live strategy implementations;
- store plaintext broker secrets in database policy documents;
- permit arbitrary live-time strategy option overrides;
- auto-approve real-money execution;
- auto-increase risk;
- replace PostgreSQL with a log-file deployment controller;
- add per-candle database logging;
- silently adopt unknown broker positions;
- abandon management of positions when an Agent is stopped;
- introduce unrestricted reflection-based plugin loading from untrusted assemblies;
- zip or repackage the repository.

---

# 26. Required final Codex report

At completion, Codex must provide a concise implementation report containing:

1. files/projects changed;
2. migrations added;
3. old static/file paths removed or retained only for migration;
4. Agent catalogue entries implemented;
5. exact simulator → profile → parity → deployment flow;
6. APIs and Dashboard components added;
7. dynamic live lifecycle behaviour;
8. position-drain/hot-swap semantics;
9. database/reporting integration;
10. tests run and results;
11. performance measurements;
12. known limitations;
13. manual commands required to apply migrations/start services;
14. no request to zip or resend the project.

---

# 27. Final design summary

The completed system should behave as follows:

```text
The simulator/research system creates immutable Agent packages.
The package contains every strategy, analysis, risk, ML, and management decision rule.
The simulator, parity runner, live shadow, Demo, and live runtime load the same package.
Market-data adapters normalize data; the Agent does not know the source.
PostgreSQL stores package revisions, permissions, deployments, commands, and audit state.
The live host dynamically resolves, warms, reconciles, and registers an Agent.
The Dashboard presents approved configured agents and a deployment wizard.
The operator selects an account, Agent revision, instruments, and mode, then starts it.
Safety gates remain server-owned and fail closed.
Open positions remain pinned to their entry package.
Automation finds and tests candidates, but executable promotion remains controlled and auditable.
```
