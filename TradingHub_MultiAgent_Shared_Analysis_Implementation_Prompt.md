# TradingHub Multi-Agent Shared Analysis Architecture — Implementation Prompt

## Role

Act as a principal quantitative trading-systems architect, senior .NET concurrency engineer, and production-grade C# implementer.

Your task is to implement a **multi-Agent runtime architecture** for TradingHub that allows:

- multiple Agent instances;
- different Agent configurations;
- parallel Agent evaluation;
- shared chart and market-analysis data;
- complete isolation of Agent state;
- deterministic candidate collection;
- safe portfolio admission;
- safe execution;
- full observability and persistence compatibility.

Use the latest attached TradingHub source as authoritative. Inspect the actual source tree first and do not trust earlier implementation reports without verification.

---

# 1. Primary objective

Implement this runtime model:

```text
One instrument market stream
        ↓
One chronological market actor
        ↓
One or more immutable analysis snapshots
        ↓
Multiple independent Agent instances
        ↓
Parallel Agent evaluation
        ↓
Deterministic decision-epoch coordination
        ↓
Portfolio and risk admission
        ↓
ExecutionManager
```

Example:

```text
EUR/USD
    Agent A:
        Improved strategy
        policy revision A
        stricter confirmation
        executable

    Agent B:
        Improved strategy
        policy revision B
        softer confirmation
        shadow-only

    Agent C:
        Legacy strategy
        policy revision C
        shadow-only
```

Compatible Agents must reuse the same immutable analysis snapshot. Agents with different underlying indicator or annotation settings must use separate analysis profiles without duplicating the market stream.

---

# 2. Non-negotiable safety rules

Enforce all of the following:

```text
Different Agent instances may evaluate concurrently.

The same Agent instance must never evaluate concurrently with itself.

Agents may share immutable market and analysis snapshots.

Agents must never share mutable state.

Agents must never place broker orders directly.

Portfolio admission must remain serialized per broker account.

Broker command submission must remain serialized per broker account.

Broker event application must remain serialized per broker account.

Only one executable Agent per instrument in the initial implementation.

Multiple shadow Agents per instrument are allowed.

No opposing executable Agents on the same instrument.

No pyramiding in the first release.

No automatic “latest policy” selection.

No live/demo/simulator branching inside Agent logic.
```

---

# 3. Required architecture

## 3.1 Agent identity

Every runtime Agent instance must have a stable identity.

Recommended shape:

```csharp
public sealed record AgentInstanceKey(
    string DeploymentId,
    InstrumentKey Instrument,
    string StrategyId,
    Guid PolicyRevisionId);
```

The identity must distinguish:

- deployment;
- instrument;
- strategy;
- policy/configuration revision.

Do not identify Agents only by strategy type or display name.

## 3.2 Analysis profile identity

Separate analysis calculation configuration from Agent interpretation configuration.

Recommended shape:

```csharp
public sealed record AnalysisProfileKey(
    string ProfileHash,
    IReadOnlySet<BarInterval> RequiredIntervals,
    int RsiPeriod,
    int AtrPeriod,
    int BollingerPeriod,
    decimal BollingerDeviation,
    int SwingLeft,
    int SwingRight,
    string FeatureSchemaHash);
```

The exact members may differ based on the codebase, but the key must include every setting that changes computed analysis.

Examples:

```text
Can share one profile:
    Agent A uses RSI threshold 55
    Agent B uses RSI threshold 60

Need different profiles:
    Agent A uses RSI period 14
    Agent B uses RSI period 21
```

## 3.3 Immutable market-analysis snapshot

Create or verify a deeply immutable snapshot:

```csharp
public sealed record MarketAnalysisSnapshot
{
    public required InstrumentKey Instrument { get; init; }
    public required AnalysisProfileKey Profile { get; init; }
    public required long SnapshotVersion { get; init; }
    public required DateTimeOffset DecisionEpoch { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }

    public required IReadOnlyDictionary<
        BarInterval,
        TimeframeAnalysisSnapshot> Timeframes { get; init; }

    public required CrossMarketSnapshot? CrossMarket { get; init; }
    public required MarketDataQualitySnapshot DataQuality { get; init; }
}
```

Do not expose:

- mutable candle buffers;
- mutable indicator instances;
- mutable annotation collections;
- live dictionaries that can change after publication;
- shared Agent state.

Every annotation must preserve:

```text
event timestamp
confirmation timestamp
first legal availability timestamp
snapshot version
```

This is required to prevent lookahead leakage.

## 3.4 Analysis profile registry

Implement a registry that:

- groups Agents by analysis profile;
- computes each required profile only once per instrument and decision epoch;
- caches completed immutable snapshots;
- avoids duplicate indicator and ChartAnnotator work;
- expires old snapshots safely;
- never mutates a published snapshot.

Recommended cache identity:

```text
Instrument
+ AnalysisProfileKey
+ DecisionEpoch
+ SnapshotVersion
```

## 3.5 Agent runtime

Each Agent runtime must own:

```text
Agent instance identity
Agent implementation
immutable policy revision
setup state
state-machine state
last evaluated epoch
last decision ID
candidate counters
rejection counters
last candidate
last status
last error
shadow outcome state
```

Recommended shape:

```csharp
public sealed class AgentRuntime
{
    public AgentInstanceKey Key { get; }
    public AnalysisProfileKey AnalysisProfile { get; }
    public AgentExecutionMode Mode { get; }

    public Task<AgentDecision> EvaluateAsync(
        MarketAnalysisSnapshot snapshot,
        CancellationToken cancellationToken);
}
```

Each runtime must serialize access to its own Agent instance.

Preferred model:

```text
One bounded mailbox per Agent runtime
One reader
Many possible producers
Sequential state transitions
```

Acceptable alternatives:

- one `SemaphoreSlim` per Agent runtime;
- one actor loop per Agent runtime.

Do not lock the whole instrument actor while Agent logic runs.

## 3.6 Agent supervisor

Implement an Agent supervisor that:

- creates Agent runtimes from deployment assignments;
- validates unique Agent identities;
- groups runtimes by instrument;
- groups runtimes by analysis profile;
- starts and stops runtimes;
- exposes status;
- supports shadow and executable modes;
- rejects unsupported assignments;
- preserves policy revision lineage;
- uses one authoritative Agent construction path.

Validate:

```text
At most one executable Agent per instrument
Any configured number of shadow Agents
No duplicate AgentInstanceKey
No missing analysis profile
No missing immutable policy revision
No incompatible calibration artifact
No execution-capable Agent without ApprovedForDemo policy
```

---

# 4. Shared chart and analysis model

## 4.1 Shared chart layers

Shared market-analysis layers include:

```text
Candles
Swings
Support/resistance zones
Channels
Trendlines
Bollinger Bands
Regime
Market structure
```

Key them by:

```text
Instrument
AnalysisProfileKey
SnapshotVersion
```

## 4.2 Agent-specific overlays

Each Agent overlay must include:

```text
AgentInstanceKey
StrategyId
PolicyRevisionId
DecisionId
CandidateId
Setup markers
Observe/Buy/Sell state
Rejection reason
Entry proposal
Stop proposal
Target proposal
Shadow/executable mode
```

The Dashboard must allow Agent overlays to be enabled and disabled independently.

Do not duplicate the complete chart per Agent when the analysis profile is the same.

For different analysis profiles, the Dashboard must clearly identify which profile produced each annotation and may allow profile switching or comparison.

---

# 5. Parallel evaluation

For each completed decision epoch:

1. build all required immutable analysis snapshots;
2. identify eligible Agent runtimes;
3. dispatch evaluation concurrently across different Agent runtimes;
4. preserve sequential access within each runtime;
5. await all expected results;
6. collect the results;
7. sort deterministically;
8. pass candidates to portfolio coordination.

Conceptual flow:

```csharp
var evaluations = eligibleAgents.Select(
    agent => agent.EvaluateAsync(
        snapshots[agent.AnalysisProfile],
        cancellationToken));

AgentDecision[] decisions =
    await Task.WhenAll(evaluations).ConfigureAwait(false);
```

This is valid only when:

- each Agent runtime has isolated state;
- snapshots are immutable;
- each runtime has its own mailbox or lock;
- no shared DbContext is used;
- no shared mutable ChartAnnotator instance is used;
- no Agent can submit orders directly.

## 5.1 Bounded concurrency

Do not create unlimited parallel tasks.

Use configurable bounded concurrency based on:

```text
CPU core count
active Agent count
analysis cost
backlog
decision deadline
```

Recommended initial default:

```text
max(2, processor_count - 1)
```

Cap it to a reasonable maximum and make it configurable.

Do not use `Task.Run` for naturally asynchronous operations. Use it only for measured CPU-bound work where it provides a real benefit.

## 5.2 Timeouts and cancellation

Every Agent evaluation must support `CancellationToken`.

A timeout must:

- not corrupt Agent state;
- not leave the runtime locked;
- produce a structured failure result;
- preserve diagnostics;
- prevent a late result from entering a later decision epoch.

Do not abandon tasks without observing completion.

Validate epoch and snapshot version before accepting results.

---

# 6. Deterministic decision-epoch coordination

Implement a `DecisionEpochCoordinator`.

Responsibilities:

- define the expected Agent set for the epoch;
- wait for required Agent results;
- support an explicit shadow-Agent timeout policy;
- reject duplicate results;
- reject stale results;
- reject mixed snapshot versions;
- produce one deterministic epoch result;
- expose missing, failed and timed-out Agent diagnostics.

Recommended identity:

```csharp
public sealed record DecisionEpochKey(
    string DeploymentId,
    InstrumentKey Instrument,
    DateTimeOffset DecisionEpoch,
    long SnapshotVersion);
```

The coordinator must not depend on task completion order.

Final candidate ordering must be deterministic.

Recommended ordering:

```text
Portfolio score descending
Expected R descending
Requested risk ascending
Instrument
StrategyId
PolicyRevisionId
DecisionId
```

Use the existing portfolio scoring policy where available.

Thread scheduling must never determine candidate priority.

---

# 7. Portfolio and execution boundary

Agents may evaluate concurrently.

Candidates must not independently reserve capital or place orders.

Required flow:

```text
Parallel Agent decisions
        ↓
Deterministic candidate set
        ↓
PortfolioCoordinator
        ↓
RiskManager
        ↓
Portfolio reservation
        ↓
ExecutionManager
```

Portfolio admission remains serialized per broker account.

Initial release rules:

```text
One executable Agent per instrument
No pyramiding
No opposing simultaneous executable positions
Maximum configured open positions
Shadow Agents never reserve capital
Shadow Agents never call ExecutionManager
```

Shadow candidates must still be persisted for outcome analysis.

---

# 8. Position ownership

Every executable position must retain:

```text
AgentInstanceKey
StrategyId
PolicyRevisionId
ManagementPolicyRevisionId
CandidateId
DecisionId
BrokerTradeId
OwnedQuantity
OpenedAt
```

The position remains pinned to the policy and management revision used when it opened.

A later policy activation affects only new candidates.

Do not change management rules for open positions unless an explicit emergency safety rule supersedes them.

---

# 9. Persistence requirements

Integrate with the PostgreSQL architecture without coupling domain code to PostgreSQL.

Persist:

## Agent assignment

```text
DeploymentId
Instrument
StrategyId
PolicyRevisionId
AnalysisProfileKey
Mode
Enabled
CreatedAt
```

## Agent evaluation

```text
DecisionId
AgentInstanceKey
DecisionEpoch
SnapshotVersion
AnalysisProfileKey
Action
StateBefore
StateAfter
Confidence
ReasonCode
Diagnostics
```

## Candidate

```text
CandidateId
DecisionId
AgentInstanceKey
PolicyRevisionId
AnalysisProfileKey
Direction
Entry reference
Stop
Target
Confidence
Regime
Calibration audit
Meta-label audit
Condition audit
Status
```

## Shadow outcome

```text
CandidateId
Target/stop outcome
MFE
MAE
Maximum achievable R
Outcome horizon
Reason
```

High-volume Agent diagnostics should use the bounded asynchronous persistence channel.

Critical portfolio and execution state remains in the critical account writer.

Never share one EF Core `DbContext` across parallel Agent tasks.

---

# 10. Configuration model

Separate two policy types.

## Analysis configuration

Controls calculation:

```text
Required intervals
Indicator periods
Swing settings
Zone settings
RANSAC settings
Channel settings
Regime calculation settings
Feature schema
```

## Agent interpretation configuration

Controls decision-making:

```text
Trend requirements
Setup thresholds
Confirmation thresholds
Price-action requirements
Feature switches
Session/spread rules
Confidence thresholds
Setup calibration artifact
Meta-model artifact
Stop/target policy
```

Store both as immutable, versioned configuration.

The Agent policy must reference the compatible analysis profile hash.

Reject incompatible combinations at startup.

---

# 11. Live, simulator and research parity

The same Agent supervisor and construction path must be used by:

```text
Simulator
Historical research
Observe-only live mode
Shadow live mode
Manual demo mode
Autonomous demo mode
```

Environment-specific hosts may select assignments and modes but must not construct strategy behaviour differently.

Verify parity for:

```text
strategy type
policy revision
analysis profile
feature switches
calibration artifacts
meta-model artifact
timeframe plan
stop/target rules
trading-condition rules
```

Any environment-specific difference must be explicit and outside the Agent.

---

# 12. Concurrency and state safety

Audit and remove unsafe patterns:

```text
static mutable Agent fields
shared mutable indicator instances
shared setup objects
shared candidate collections
shared DbContext
global mutable ChartAnnotator state
parallel calls into one Agent instance
mutable snapshots
unordered dictionaries affecting decisions
late results entering later epochs
```

Every mutable object must have one clear owner.

Recommended ownership:

```text
InstrumentMarketActor:
    chronological candle updates

AnalysisProfileRuntime:
    mutable indicator/annotation calculation state

Published snapshot:
    immutable

AgentRuntime:
    Agent state machine

DecisionEpochCoordinator:
    epoch result collection

PortfolioCoordinator:
    capital/risk admission

ExecutionManager:
    broker commands
```

---

# 13. Failure handling

## Analysis failure

If one analysis profile fails:

- mark that profile unavailable;
- do not affect Agents using other profiles;
- emit structured diagnostics;
- do not evaluate dependent Agents;
- do not reuse stale analysis silently.

## Agent failure

If one Agent fails:

- isolate the failure;
- preserve other Agent results;
- mark the Agent unhealthy;
- emit structured diagnostics;
- use bounded restart/backoff;
- do not allow partial state corruption.

Executable Agents fail closed.

## Epoch timeout

If an executable Agent misses its deadline:

- do not use a stale previous decision;
- mark the epoch incomplete for that Agent;
- do not execute that Agent's candidate;
- preserve completed shadow results;
- record the timeout reason.

## Persistence failure

Agent evaluation may continue in Observe/Shadow only where safely buffered and explicitly allowed.

New executable entries must pause when critical persistence is unavailable.

---

# 14. Diagnostics and status API

Expose per-Agent runtime status:

```text
AgentInstanceKey
Instrument
StrategyId
PolicyRevisionId
AnalysisProfileKey
Mode
Health
Current state
Last evaluated epoch
Last snapshot version
Candidates observed
Buy candidates
Sell candidates
Rejected by setup
Rejected by setup calibration
Rejected by meta-model
Rejected by trading conditions
Last candidate
Last rejection
Last error
Average evaluation duration
p95 evaluation duration
Mailbox depth
Timeout count
```

Expose per-analysis-profile status:

```text
ProfileHash
Instrument
Required intervals
Last snapshot version
Last available time
Build duration
Cache hits
Cache misses
Failure count
Dependent Agent count
```

Expose epoch status:

```text
Expected Agents
Completed Agents
Failed Agents
Timed-out Agents
Candidate count
Epoch duration
```

---

# 15. Required tests

## Agent isolation

- two Agents with different configurations do not share state;
- one Agent reset does not reset another;
- one Agent exception does not corrupt another;
- counters remain independent;
- long/short state cannot leak;
- same strategy type with different policy revisions remains isolated.

## Shared snapshot

- compatible Agents receive the same immutable snapshot;
- snapshot cannot be mutated;
- different profiles receive different snapshots;
- identical profiles compute analysis once;
- cache keys include instrument, profile and version;
- stale snapshots are rejected.

## Parallel determinism

- sequential and parallel evaluation produce identical decisions;
- task completion order does not affect candidate ordering;
- repeated runs produce identical IDs and outcomes;
- dictionary order does not affect results;
- bounded concurrency does not change behaviour.

## Same-Agent serialization

- concurrent evaluation requests for one Agent are processed sequentially;
- no overlapping state transitions;
- cancellation releases the runtime;
- late results are rejected;
- duplicate epoch requests do not emit duplicate candidates.

## Multi-Agent configuration

- different thresholds share one analysis profile;
- different RSI periods create separate profiles;
- different swing settings create separate profiles;
- incompatible policy/profile combinations fail fast;
- feature switches remain Agent-specific.

## Portfolio safety

- two executable Agents cannot exist for one instrument;
- shadow Agents cannot reserve risk;
- parallel Agent candidates cannot oversubscribe portfolio limits;
- deterministic ordering is stable;
- duplicate candidates cannot create duplicate reservations.

## Live/simulator parity

- the same assignments create equivalent runtimes;
- policy hashes match;
- profile hashes match;
- artifacts match;
- decisions match for identical snapshots.

## Dashboard overlays

- shared chart data is not duplicated unnecessarily;
- Agent overlays are independently selectable;
- markers identify strategy and policy;
- different profiles are clearly labelled.

## Performance

Test at least:

```text
20 instruments
2 analysis profiles per instrument
4 Agents per instrument
80 total Agent runtimes
1-minute decision epochs
parallel evaluation
shadow diagnostics
PostgreSQL async batching enabled
```

Measure:

```text
analysis build time
cache hit ratio
Agent evaluation p50/p95/p99
epoch completion time
mailbox depth
CPU
memory
GC pressure
persistence queue depth
```

---

# 16. Performance requirements

The implementation should achieve:

```text
No duplicated analysis for identical profile/instrument/epoch
No database call per indicator or quote
No unbounded task creation
No unbounded channels
Minimal snapshot copying
No shared mutable state
No blocking waits
No .Result or .Wait()
No Task.Run around database I/O
No one-thread-per-Agent design
```

Use:

```text
Task
async/await
ValueTask where justified
Channel<T>
Task.WhenAll
bounded SemaphoreSlim
immutable records
pooled collections only after measurement
Npgsql async APIs
batched persistence
```

Correctness and determinism are more important than small allocation reductions.

---

# 17. Implementation order

## Phase 1 — audit

- map current Agent construction;
- map ChartAnnotator ownership;
- identify shared mutable state;
- identify simulator/live differences;
- identify current parallelism;
- identify duplicate analysis work.

Produce findings before changing code.

## Phase 2 — immutable snapshot contract

- define analysis profile key;
- define immutable snapshot;
- add versioning and legal availability;
- add tests.

## Phase 3 — analysis profile registry

- group Agents by profile;
- compute once;
- cache safely;
- add failure isolation;
- add tests.

## Phase 4 — Agent runtime isolation

- create runtime identity;
- isolate state;
- serialize same-Agent evaluation;
- add status and diagnostics;
- add tests.

## Phase 5 — supervisor and assignments

- construct multiple Agents;
- validate modes;
- enforce one executable Agent per instrument;
- add deployment integration.

## Phase 6 — parallel epoch evaluation

- add bounded concurrency;
- add deterministic collection;
- add timeout/cancellation handling;
- add tests.

## Phase 7 — portfolio integration

- pass only executable candidates;
- preserve shadow candidates;
- ensure serialized admission;
- add safety tests.

## Phase 8 — persistence and Dashboard

- persist assignments/evaluations/outcomes;
- add status DTOs;
- add chart overlays;
- add performance metrics.

## Phase 9 — parity and load validation

- simulator/live parity tests;
- deterministic replay tests;
- high-load soak test;
- memory and CPU review.

---

# 18. Deliverables

Return:

1. updated source archive;
2. implementation report;
3. architecture report;
4. test report;
5. performance report;
6. issue register;
7. SHA-256 checksum.

The implementation report must include:

```text
Files created
Files changed
Architecture decisions
Concurrency model
State ownership map
Snapshot/cache model
Agent construction parity
Tests added
Build/test commands
Known limitations
Deferred work
```

Include Mermaid diagrams for:

1. market stream to shared analysis;
2. analysis profile grouping;
3. Agent supervisor;
4. parallel decision epoch;
5. portfolio boundary;
6. state ownership;
7. Dashboard chart/overlay model;
8. failure isolation.

---

# 19. Acceptance criteria

The work is complete only when all are true:

```text
Multiple differently configured Agents can run simultaneously.

Compatible Agents share one immutable analysis snapshot.

Incompatible analysis settings create separate profiles.

Different Agents evaluate concurrently.

The same Agent never evaluates concurrently with itself.

Agent results are deterministic regardless of task completion order.

No Agent shares mutable state with another Agent.

No Agent submits broker orders directly.

Only one executable Agent per instrument is allowed initially.

Multiple shadow Agents are supported.

Portfolio admission remains serialized per account.

Simulator and live use the same Agent construction path.

Agent overlays can be displayed separately on one shared chart.

All decisions preserve Agent instance and policy lineage.

All critical concurrency and isolation tests pass.
```

---

# 20. Final review questions

End the implementation report by answering:

1. Can multiple Agent instances with different configurations run safely in parallel?
2. Which data is shared?
3. Which state is isolated?
4. How is identical analysis reused?
5. How are different analysis profiles separated?
6. How is same-Agent concurrency prevented?
7. How is deterministic ordering enforced?
8. How is portfolio oversubscription prevented?
9. How are shadow and executable Agents separated?
10. Can simulator and live produce identical decisions?
11. Can one Agent fail without affecting another?
12. Can the Dashboard show all Agent decisions on one chart?
13. What remains before allowing multiple executable Agents on the same instrument?
14. What are the current performance limits?
15. Is the implementation safe enough for OANDA Practice testing?

Be strict, evidence-based and conservative.

Do not increase trade count by weakening strategy rules.

Do not introduce new indicators or models unless required for correctness.

Do not allow concurrency to change trading behaviour.

Do not use parallelism where serialization is required for account safety.
