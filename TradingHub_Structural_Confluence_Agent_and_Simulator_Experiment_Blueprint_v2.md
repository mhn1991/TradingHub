# TradingHub Structural Confluence Shadow Agent and Simulator Experiment System
## Production Implementation Blueprint for Codex — Revision 2

**Repository snapshot reviewed:** `TradingHub_source(4).zip`, 18 July 2026  
**Primary strategy ID:** `structural-confluence`  
**Initial strategy version:** `structural-confluence-v1`  
**Blueprint revision:** 2 — adds profile experiments, bounded parallel execution, and leakage-safe learning/evaluation timelines  
**Target framework:** existing repository target (`net10.0`)  

> **Do not zip, copy, or repackage the repository.** Work directly in the existing local repository. Commit-quality source changes, tests, documentation, and configuration examples are required; no archive deliverable is needed.

---

## 1. Objective

Implement a new production-quality trading agent that composes the structural disciplines already present in TradingHub:

- price-derived supply and demand;
- price-inferred liquidity pools, sweeps, accepted breaks, and retests;
- price action and market structure;
- NeoWave context;
- market-regime classification;
- CCI, RSI, Bollinger Bands, ADX/DMI, efficiency ratio, ATR, and volume;
- existing setup calibration and calibrated meta-label model;
- existing risk, portfolio, execution, trade-management, simulator, research, dashboard, and live-host infrastructure.

The new agent must not be a single opaque score that lets unrelated indicators independently vote for Buy or Sell. It must contain explicit, testable playbooks with clear state machines and structural invalidation.

The first three playbooks are:

1. **Liquidity Sweep Reversal**
2. **Supply/Demand Pullback Continuation**
3. **Liquidity Accepted-Break Retest Continuation**

The same strategy implementation and promoted policy must operate consistently in:

```text
Dashboard Simulator
BacktestRunner CLI
QuantResearch / QuantResearchRunner
LiveTradingHost ObserveOnly/Shadow
LiveTradingHost ManualApproval/Automatic after later promotion
```

The initial operational deployment mode is **live shadow**, with no broker writes.

---

## 2. Current repository facts that the implementation must respect

### 2.1 Existing environment-neutral Agent contract

The new agent must implement:

- `Agent/Abstractions/ITradingAgent.cs`
- `ITradingAgent.Name`
- `ITradingAgent.RequiredIntervals`
- `ITradingAgent.TriggerInterval`
- `ITradingAgent.ExitManagementMode`
- `ITradingAgent.EvaluateAsync(AgentMarketContext, CancellationToken)`

Do not introduce broker, simulator, database, or live-host dependencies into the `Agent` project.

### 2.2 Current analysis data is already available

`AgentMarketContext.Analysis` exposes `MultiTimeframeAnalysis`, whose `AnalysisSnapshot` values already contain:

- `MarketStructure`
- `PriceAction`
- `MarketRegime`
- `NeoWave`
- `SupplyDemand`
- `Liquidity`
- `SupplyDemandLiquidityConfluence`
- `ValueReferences`
- `IndicatorSnapshot`

`IndicatorSnapshot` currently includes raw CCI but does not expose the richer CCI relationship state needed by this agent.

### 2.3 Structural detectors are causal

Supply/demand zones and liquidity objects carry `ConfirmedAt` and `AvailableAt`. The new agent must only observe objects where:

```csharp
item.AvailableAt <= context.Timestamp
```

Never use a zone, pool, sweep, accepted break, retest, price-action event, swing, or relationship before its availability/confirmation time.

### 2.4 Existing structural evidence is not an entry engine

`Agent/Strategies/StructuralEvidencePolicy.cs` currently augments progressive-agent decisions after an entry candidate exists. It must remain available for legacy/improved agents.

Do not mutate `StructuralEvidencePolicy` into the new strategy engine. The structural-confluence agent needs its own playbook composer because liquidity and supply/demand are its primary setup definitions rather than post-candidate annotations.

Reusable geometry/evidence helpers may be extracted only when behaviour remains byte-compatible for existing agents and tests.

### 2.5 Existing calibrated “ML”

The current codebase uses deterministic calibration cohorts rather than a general ML framework:

- `RiskManager/Calibration/SetupCalibration.cs`
- `RiskManager/Calibration/MetaLabel.cs`
- `Calibration/CalibratedSetupMetaModel.cs`
- `Calibration/MetaModelArtifact.cs`
- `QuantResearch.Training/Experiments/MetaModelCalibrator.cs`

The model currently buckets by:

- strategy ID;
- regime;
- setup confidence;
- multi-timeframe alignment.

It can accept, reject, or reduce risk. It must continue to be incapable of increasing risk above 1.0.

### 2.6 Current strategy discovery is duplicated and hard-coded

The repository currently assumes only `legacy` and `improved` in several places, including:

- `Simulator/Models/BacktestConfiguration.cs`
- `Simulator/Services/BacktestApplicationService.cs`
- `DashboardLive/SimulationApi.cs`
- `BacktestRunner/BacktestCommandOptions.cs`
- `BacktestRunner/Program.cs`
- `QuantResearch.Training/Pipeline/PreRunCalibrationService.cs`
- `QuantResearch.Training/Pipeline/LiveCalibrationTrainingScheduler.cs`
- `Dashboard/src/components/SimulatorPanel.vue`
- `Dashboard/src/components/ResearchPanel.vue`
- `TradingPolicies/TradingPolicyProfile.cs`
- `Simulator/Models/TradingPolicyPromotion.cs`
- `LiveTrading/Agents/LiveAgentFactory.cs`
- `LiveTradingHost/LiveEngineHostedService.cs`

Do not add another set of scattered switch statements. Introduce one shared agent definition/factory mechanism and migrate these consumers to it.

### 2.7 Current live `Shadow` mode records candidates but does not generate paper-trade outcomes

`LiveTrading/Runtime/LiveTradingRuntimeCoordinator.ProcessEpochAsync` currently writes `shadow-candidates` with `NonExecutingDeploymentMode`. That is safe, but it does not create virtual fills, positions, stops, targets, MFE, MAE, or outcomes.

This implementation must preserve the broker-write prohibition and add a separate true shadow outcome path.

---

## 3. Non-negotiable invariants

1. **No look-ahead**
   - consume only completed candles and objects whose `AvailableAt` is not in the future;
   - state transitions occur only on the current decision epoch;
   - tests must prove future objects cannot influence earlier decisions.

2. **Determinism**
   - stable ordering by timestamp and stable IDs;
   - no random tie-breaking;
   - parallel workers must produce the same results as sequential workers;
   - state must be isolated per agent instance/instrument.

3. **Structural strategy owns setup validity**
   - ML cannot invent Buy/Sell;
   - CCI cannot independently create a trade;
   - an indicator cannot repair invalid stop/target geometry;
   - a strong score cannot override a missing mandatory component.

4. **ML is reduce-only**
   - `RiskMultiplier` remains within `[0, 1]`;
   - negative expected R may reject;
   - weak expectancy may reduce risk;
   - strong expectancy may retain base risk, never increase it.

5. **One code path across environments**
   - simulator, CLI, research, and live host construct the same `ITradingAgent` from the same policy definition;
   - do not reconstruct separate live-only structural options.

6. **Shadow cannot write to the broker**
   - preserve `ShadowExecutionCoordinator` and `ShadowBrokerClient` protections;
   - live paper outcomes must be calculated in a separate paper ledger, never by invoking broker write APIs.

7. **Existing agents remain compatible**
   - legacy/improved defaults and results must not change unless explicitly required by a shared schema migration;
   - all existing tests must continue to pass.

8. **Entry-pinned management**
   - open positions retain the entry policy revision and structural references;
   - a policy hot-swap cannot silently replace an existing trade’s thesis.

9. **Bounded memory**
   - all per-agent histories and paper ledgers must use bounded collections or persisted compaction;
   - no unbounded event accumulation in live memory or SSE payloads.

10. **No silent configuration fallback**
    - invalid strategy IDs, mismatched policy kinds, incompatible calibration artifacts, and missing structural detector settings must fail with actionable messages.

---

## 4. Target architecture

```text
Completed candles
    ↓
ChartAnnotationEngine
    ├── structure / price action
    ├── supply-demand
    ├── liquidity events
    ├── regime / NeoWave
    └── indicators + CCI analysis
    ↓
MultiTimeframeAnalysis
    ↓
StructuralConfluenceAgent
    ├── StructuralEvidencePacketFactory
    ├── LiquiditySweepReversalPlaybook
    ├── SupplyDemandPullbackPlaybook
    ├── LiquidityBreakRetestPlaybook
    ├── PlaybookStateStore
    ├── StructuralCandidateArbitrator
    └── StructuralGeometryBuilder
    ↓
AgentDecision
    ↓
SafeTradingPipeline
    ├── data quality
    ├── trading conditions
    ├── setup calibration
    ├── calibrated meta-label model
    └── account safety
    ↓
Simulator / portfolio coordinator / live shadow outcome service
```

---

## 5. Shared agent catalogue and policy definition

### 5.1 Add a generic agent kind

Create under `Agent/Factories` or `Agent/Configuration`:

```csharp
public enum TradingAgentKind
{
    LegacyProgressive,
    ImprovedProgressive,
    StructuralConfluence
}
```

Retain `ProgressiveAgentKind` temporarily for compatibility inside `ProgressiveAgentFactory`, but application-layer code must move to `TradingAgentKind`.

### 5.2 Add a discriminated agent definition

Create:

`Agent/Configuration/TradingAgentDefinition.cs`

```csharp
public sealed record TradingAgentDefinition
{
    public required TradingAgentKind Kind { get; init; }
    public ProgressiveStrategyOptions? Progressive { get; init; }
    public StructuralConfluenceStrategyOptions? StructuralConfluence { get; init; }

    public void Validate();
}
```

Validation rules:

- `LegacyProgressive` and `ImprovedProgressive` require `Progressive != null` and `StructuralConfluence == null`;
- `StructuralConfluence` requires `StructuralConfluence != null` and `Progressive == null`;
- exactly one option branch must be populated.

### 5.3 Add one factory

Create:

`Agent/Factories/TradingAgentFactory.cs`

```csharp
public static class TradingAgentFactory
{
    public static ITradingAgent Create(TradingAgentDefinition definition);
}
```

Implementation:

- legacy delegates to `ProgressiveAgentFactory.Create(ProgressiveAgentKind.Legacy, ...)`;
- improved delegates to `ProgressiveAgentFactory.Create(ProgressiveAgentKind.Improved, ...)`;
- structural creates `new StructuralConfluenceAgent(...)`.

### 5.4 Add canonical string IDs

Create:

`Agent/Configuration/TradingAgentTypeIds.cs`

Constants:

```text
legacy
improved
structural-confluence
```

Provide:

- exact case-insensitive parse;
- canonical formatting;
- aliases `legacy-progressive` and `improved-progressive` only for backward compatibility;
- no fuzzy `Contains("legacy")` or `Contains("improved")` parsing in new code.

### 5.5 Migrate hosts to the shared factory

Replace hard-coded strategy construction and validation in the files listed in section 2.6.

Do not leave one environment using `ProgressiveAgentFactory` directly while another uses the new factory.

### 5.6 Trading policy compatibility

Refactor `TradingPolicies/TradingPolicyProfile.cs` to carry an effective `TradingAgentDefinition`.

Recommended migration-safe shape:

```csharp
public int SchemaVersion { get; init; } = 2;
public TradingAgentDefinition? AgentDefinition { get; init; }

// Temporary legacy deserialization fields:
public ProgressiveAgentKind? AgentKind { get; init; }
public ProgressiveStrategyOptions? AgentOptions { get; init; }
```

Add an `EffectiveAgentDefinition()` resolver:

- schema 2 profiles use `AgentDefinition`;
- legacy profiles may resolve `AgentKind + AgentOptions`;
- reject conflicting simultaneous definitions;
- configuration hash must use the normalized effective definition, not both representations.

Newly promoted profiles must write schema 2 only.

Update:

- `Simulator/Models/TradingPolicyPromotion.cs`
- `LiveTradingHost/Configuration/LivePolicyBundleFactory.cs`
- `LiveTrading/Agents/LiveAgentFactory.cs`
- `LiveTradingHost/LiveEngineHostedService.cs`

The live host must construct the agent through `TradingAgentFactory` from the profile’s effective definition.

---

## 6. Structural-confluence strategy options

Create:

`Agent/Strategies/StructuralConfluence/StructuralConfluenceStrategyOptions.cs`

Suggested structure:

```csharp
public sealed record StructuralConfluenceStrategyOptions
{
    public BarInterval ContextInterval { get; init; } = BarInterval.Hours(1);
    public IReadOnlyList<BarInterval> AdditionalContextIntervals { get; init; } = [];
    public BarInterval SetupInterval { get; init; } = BarInterval.Minutes(15);
    public BarInterval TriggerInterval { get; init; } = BarInterval.Minutes(5);

    public decimal Quantity { get; init; } = 1_000m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;
    public decimal StopBufferAtr { get; init; } = 0.20m;
    public decimal TargetBufferAtr { get; init; } = 0.10m;
    public int MaximumTriggerBars { get; init; } = 12;
    public int MaximumArmedSetupBars { get; init; } = 24;

    public LiquiditySweepReversalOptions LiquiditySweepReversal { get; init; } = new();
    public SupplyDemandPullbackOptions SupplyDemandPullback { get; init; } = new();
    public LiquidityBreakRetestOptions LiquidityBreakRetest { get; init; } = new();

    public StructuralContextOptions Context { get; init; } = new();
    public StructuralTriggerOptions Trigger { get; init; } = new();
    public StructuralConfirmationOptions Confirmation { get; init; } = new();
    public StructuralArbitrationOptions Arbitration { get; init; } = new();
    public StructuralGeometryOptions Geometry { get; init; } = new();

    public string StrategyVersion { get; init; } = "structural-confluence-v1";

    public IReadOnlySet<BarInterval> RequiredIntervals { get; }
    public void Validate();
}
```

### 6.1 Default timeframe relationship

Validation must require:

```text
TriggerInterval < SetupInterval < ContextInterval
```

Additional context intervals must be unique and no finer than setup.

### 6.2 Playbook options

#### `LiquiditySweepReversalOptions`

Include at least:

- `Enabled = true`
- `MinimumPoolQuality`
- allowed `LiquidityPoolType` values;
- `MinimumSweepPenetrationAtr`
- `MaximumSweepPenetrationAtr`
- `RequireClosedBackInside = true`
- `MaximumBarsSinceSweep`
- `RequireSupplyDemandConfluence`
- `PreferredSupplyDemandConfluence`
- `MaximumZonePoolDistanceAtr`
- `MinimumReclaimBodyRatio`
- `RequirePriceActionTrigger = true`
- `RequireMicroStructureBreak` configurable;
- `CciMode` (`Disabled`, `Soft`, `Required`)
- `MinimumConfidence`.

#### `SupplyDemandPullbackOptions`

Include:

- `Enabled = true`
- `MinimumZoneQuality`
- permitted zone states, default `ConfirmedFresh`, `Approached`, `Tested` with strict touch limits;
- `MaximumPriorTouches`
- `MaximumPenetrationRatio`
- `RequireTrendAlignment = true`
- `RequirePriceActionTrigger = true`
- `CciMode`
- `AllowRsiAlternativeConfirmation`
- `AllowBollingerReEntryConfirmation`
- `MinimumConfidence`.

#### `LiquidityBreakRetestOptions`

Include:

- `Enabled = true`
- `MinimumPoolQuality`
- `MaximumBarsSinceAcceptedBreak`
- `MinimumAcceptanceCloses`
- `MinimumDisplacementAtr`
- `MaximumRetestDistanceAtr`
- `RequireRetestEvent` where available;
- `RequirePriceActionTrigger = true`
- `CciMode`
- minimum ADX/ER expansion conditions as optional soft or required settings;
- `MinimumConfidence`.

### 6.3 Confirmation mode

Create a reusable enum:

```csharp
public enum StructuralConfirmationMode
{
    Disabled,
    Soft,
    Required
}
```

Do not reuse `PriceActionConfirmationMode` for CCI because the semantics differ.

---

## 7. CCI analysis upgrade in ChartAnnotator

Raw `IndicatorSnapshot.Cci` is insufficient for reliable setup confirmation and research attribution.

### 7.1 Add models

In `ChartAnnotator/Models/AnalysisModels.cs`, add:

```csharp
public enum CciZone
{
    Unknown,
    ExtremeNegative,
    Negative,
    Neutral,
    Positive,
    ExtremePositive
}

public enum CciRelationshipType
{
    None,
    RegularBullishDivergence,
    RegularBearishDivergence,
    HiddenBullishDivergence,
    HiddenBearishDivergence,
    BullishConvergence,
    BearishConvergence
}

public sealed record CciRelationshipSnapshot { ... }

public sealed record CciAnalysisSnapshot
{
    public static CciAnalysisSnapshot Empty { get; } = new();
    public CciZone Zone { get; init; }
    public MomentumDirection MomentumDirection { get; init; }
    public decimal? MomentumChange { get; init; }
    public decimal? PreviousValue { get; init; }
    public bool CrossedUpFromExtremeNegative { get; init; }
    public bool CrossedDownFromExtremePositive { get; init; }
    public bool CrossedUpZero { get; init; }
    public bool CrossedDownZero { get; init; }
    public int BarsSinceExtremeNegative { get; init; } = -1;
    public int BarsSinceExtremePositive { get; init; } = -1;
    public CciRelationshipSnapshot? LatestRelationship { get; init; }
    public bool IsNewRelationship { get; init; }
    public int SampleCount { get; init; }
}
```

Add:

```csharp
public CciAnalysisSnapshot CciAnalysis { get; init; } = CciAnalysisSnapshot.Empty;
```

to `IndicatorSnapshot` and the replay `IndicatorPoint` representation.

### 7.2 Add state

Create:

`ChartAnnotator/Indicators/CciAnalysisState.cs`

Mirror the causal approach in `RsiAnalysisState`:

- bounded ring buffer of CCI samples;
- bounded confirmed high/low pivot buffers;
- compare CCI values at confirmed price swing pivot timestamps;
- emit relationship only after the swing’s right-side confirmation exists;
- expire relationships after configurable lifetime;
- calculate momentum over a configurable lookback;
- calculate threshold crossings without future values.

Suggested default thresholds:

```text
Extreme negative: <= -100
Extreme positive: >= +100
```

Make thresholds configurable, not constants hidden in the strategy.

### 7.3 Add annotation options

Extend `ChartAnnotator/Engine/ChartAnnotationOptions.cs` with:

- `CciMomentumLookback`
- `CciMomentumThreshold`
- `CciExtremeNegativeThreshold`
- `CciExtremePositiveThreshold`
- `CciMinimumDivergenceDifference`
- `CciMinimumPriceDifferenceAtr`
- `CciSignalLifetimeCandles`

Update validation and `ChartAnnotationOptionsHasher` so every new field participates in the analysis profile hash.

### 7.4 Wire the engine

Update `ChartAnnotator/Engine/ChartAnnotationEngine.cs`:

- construct `CciAnalysisState` in per-chart state;
- call it after current CCI and confirmed swings are available;
- publish `CciAnalysis` in `IndicatorSnapshot`;
- add it to indicator replay/history projection;
- no extra full-history scan per candle.

### 7.5 Dashboard chart

`Dashboard/src/components/AnalysisChart.vue` already renders CCI. Extend tooltips/details to show:

- zone;
- rising/falling/stable;
- recent threshold crossing;
- latest CCI relationship;
- relationship age/strength.

Do not add large relationship collections to every SSE frame. Publish only the current bounded summary in the existing indicator snapshot.

---

## 8. Evidence packet

Create under:

`Agent/Strategies/StructuralConfluence/Evidence`

### 8.1 `StructuralEvidencePacket`

The packet must be immutable and contain only decision-time information:

```csharp
public sealed record StructuralEvidencePacket
{
    public required InstrumentKey Instrument { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required AnalysisSnapshot Context { get; init; }
    public required AnalysisSnapshot Setup { get; init; }
    public required AnalysisSnapshot Trigger { get; init; }
    public required IReadOnlyList<AnalysisSnapshot> AdditionalContexts { get; init; }

    public required StructuralContextEvidence ContextEvidence { get; init; }
    public required SupplyDemandPacket SupplyDemand { get; init; }
    public required LiquidityPacket Liquidity { get; init; }
    public required TriggerPacket TriggerEvidence { get; init; }
    public required IndicatorConfirmationPacket Indicators { get; init; }
}
```

### 8.2 `StructuralEvidencePacketFactory`

Responsibilities:

- retrieve exact configured intervals from `MultiTimeframeAnalysis`;
- fail to `Observe` with reason code `StructuralAnalysisNotReady` if mandatory snapshots are absent;
- filter every event/object by `AvailableAt <= context.Timestamp`;
- sort deterministically;
- select active and recent structural candidates without mutating detector output;
- calculate normalized ATR distances using the setup or trigger ATR explicitly;
- never treat unavailable data as neutral evidence if the playbook requires it; distinguish `Unavailable`, `Neutral`, `Aligned`, and `Conflicting`.

### 8.3 Evidence-role separation

Use the following roles:

| Role | Source |
|---|---|
| Context | regime, market structure, NeoWave, ADX, ER, higher-timeframe direction |
| Location | supply/demand, price zones, anchored value references |
| Catalyst | liquidity sweep, accepted break, retest, zone touch/rejection |
| Trigger | price action event/setup, break/retest trigger, micro structure change |
| Confirmation | CCI, RSI, Bollinger, DMI, volume |
| Geometry | sweep extreme, zone distal/proximal, target pool, opposing zone |

Do not merge these into one untyped list of reason strings internally. Reason codes are output/audit, not the domain model.

---

## 9. Playbook contract and lifecycle

Create:

`Agent/Strategies/StructuralConfluence/Playbooks/IStructuralPlaybook.cs`

```csharp
public interface IStructuralPlaybook
{
    string PlaybookId { get; }
    string Version { get; }

    PlaybookEvaluation Evaluate(
        StructuralEvidencePacket evidence,
        PlaybookRuntimeState state);
}
```

`PlaybookEvaluation` must include:

- direction;
- lifecycle transition;
- setup identity;
- structural references;
- mandatory-gate results;
- supporting/conflicting evidence;
- component qualities;
- proposed entry/stop/targets;
- confidence;
- expiration;
- reason code;
- whether a candidate is ready.

### 9.1 Stable playbook IDs

Use:

```text
structural.liquidity-sweep-reversal
structural.supply-demand-pullback
structural.liquidity-break-retest
```

Keep playbook ID separate from deployment strategy ID (`structural-confluence`).

### 9.2 State storage

Create:

- `PlaybookStateStore`
- bounded state per instrument and playbook;
- state only advances on strictly newer `(AvailableAt, snapshot version)`;
- duplicate evaluation must be idempotent;
- generated setup IDs must be stable hashes of structural source IDs and event time.

State must not be static/global.

### 9.3 Common lifecycle values

Create:

```csharp
public enum StructuralSetupLifecycle
{
    Dormant,
    Armed,
    CatalystObserved,
    AwaitingTrigger,
    CandidateProduced,
    Invalidated,
    Expired
}
```

Playbooks may expose more detailed internal phases, but persisted/public status must map to this common lifecycle.

---

## 10. Playbook 1 — Liquidity Sweep Reversal

### 10.1 Intent

Trade a failed excursion through pre-existing liquidity, in the opposite direction of the sweep, only after reclaim and trigger confirmation.

### 10.2 Bullish sequence

```text
Pre-existing sell-side liquidity pool
→ price penetrates below the pool
→ detector confirms Sweep, not AcceptedBreak
→ price closes back inside/above pool
→ optional demand-zone confluence
→ bullish trigger or micro structure break
→ CCI recovery / divergence confirmation
→ Buy candidate
```

Bearish is symmetrical around buy-side liquidity.

### 10.3 Mandatory gates

1. Pool was available before sweep start.
2. Pool quality meets minimum.
3. Pool type is allowed.
4. Sweep event belongs to that pool and is available now.
5. `ClosedBackInside == true` by default.
6. Sweep penetration is between configured ATR bounds.
7. Event age is within `MaximumBarsSinceSweep`.
8. No accepted-break event supersedes the sweep.
9. Trigger exists within the bounded trigger window.
10. Stop and target geometry is valid after costs.
11. Minimum reward/risk is satisfied against the **nearest** real obstacle.

### 10.4 Supply/demand confluence

For bullish:

- prefer a demand zone overlapping the liquidity pool or lying within configured ATR distance;
- zone must be available before decision;
- reject invalidated/mitigated/expired zones;
- use zone freshness, quality, touch count, and penetration as typed evidence.

For bearish, use supply.

If confluence mode is `Preferred`, absence is neutral and presence improves confidence. If `Required`, absence blocks the candidate.

### 10.5 Trigger

Accepted bullish trigger examples:

- bullish engulfing or rejection event;
- triggered bullish composite price-action setup;
- bullish break-and-retest;
- confirmed break of a minor swing high after the reclaim;
- bullish market-structure change on trigger interval.

Use existing `PriceActionSnapshot` and `MarketStructureSnapshot`; do not re-detect candlestick patterns in the agent.

### 10.6 CCI interpretation

Bullish aligned states include:

- CCI crossed upward from extreme negative;
- CCI is rising after an extreme negative print;
- regular bullish CCI divergence;
- bullish convergence after reclaim.

Bullish conflict includes:

- CCI still falling materially below the negative extreme;
- recent regular bearish divergence on the trigger side;
- strong negative momentum with no recovery.

CCI soft mode may add/subtract bounded confidence. Required mode must demand one aligned state and reject direct conflict.

### 10.7 Geometry

Bullish stop preference order:

1. below sweep extreme plus ATR/spread buffer;
2. below overlapping demand distal boundary if farther and still within maximum stop distance;
3. reject if neither is valid.

Bullish targets:

1. nearest internal swing high or local structure target for partial exit;
2. nearest active buy-side liquidity pool above entry;
3. next opposing supply zone for runner target.

The bracket target used for minimum R:R must be the nearest selected structural barrier, not a farther target chosen to manufacture a better ratio.

---

## 11. Playbook 2 — Supply/Demand Pullback Continuation

### 11.1 Intent

Trade a pullback into a fresh price-derived zone while aligned with higher-timeframe context.

### 11.2 Bullish sequence

```text
Bullish context / no strong bearish veto
→ fresh demand zone formed by displacement
→ price approaches/touches zone
→ zone remains valid
→ bearish pressure fails below distal boundary
→ bullish trigger
→ CCI/RSI/Bollinger confirmation
→ Buy candidate
```

Bearish is symmetrical.

### 11.3 Mandatory gates

1. Context is aligned or at least not strongly opposed according to options.
2. Zone type matches direction.
3. Zone quality and freshness meet minimum.
4. Zone state is permitted.
5. Prior touch count does not exceed limit.
6. Penetration does not exceed configured threshold.
7. No invalidation event exists at or before decision time.
8. Trigger exists after the current approach/touch event.
9. Structural stop and nearest target satisfy minimum R:R.

### 11.4 Context

Preferred bullish context:

- `TrendingUp` or `BreakoutExpansionUp`;
- bullish market structure;
- positive DMI direction with sufficient ADX;
- acceptable efficiency ratio;
- NeoWave bullish or neutral, not high-confidence bearish conflict.

Range context may be allowed only when the zone is at a meaningful range boundary and the option explicitly allows it.

### 11.5 Indicator confirmation

Do not require all indicators.

Default policy:

```text
Mandatory: location + valid zone reaction + price-action trigger
Confirmation: at least one independent aligned confirmation when mode is Required
```

Examples:

- CCI recovery;
- RSI divergence or momentum recovery;
- Bollinger close back inside after lower-band extension;
- relative-volume increase on rejection;
- DMI direction supporting the context.

CCI and RSI are both momentum oscillators; do not count them as two fully independent structural confirmations in confidence calculation.

### 11.6 Geometry

Stop:

- beyond zone distal boundary plus ATR buffer;
- optional tighter trigger-swing stop only if it does not sit inside the zone or known liquidity pool;
- use existing liquidity stop-avoidance logic conceptually, but keep implementation local or extract a pure helper.

Targets:

- nearest opposing internal liquidity;
- next buy-side/sell-side liquidity pool;
- opposing supply/demand zone.

---

## 12. Playbook 3 — Liquidity Accepted-Break Retest Continuation

### 12.1 Intent

Distinguish a real accepted break from a liquidity hunt and trade continuation after a successful retest.

### 12.2 Bullish sequence

```text
Pre-existing buy-side liquidity
→ displacement breaks above pool
→ AcceptedBreak event / sustained closes above
→ optional Bollinger expansion and ADX/ER improvement
→ price retests broken level from above
→ retest holds
→ bullish trigger
→ CCI remains constructive or recovers through zero
→ Buy candidate
```

### 12.3 Mandatory gates

1. Pool existed before the break.
2. Accepted-break evidence exists; a simple touch is insufficient.
3. Displacement meets minimum ATR/body quality.
4. No immediate failure/reclaim event invalidates acceptance.
5. Retest occurs within configured age and distance.
6. Retest remains on the accepted side of the level.
7. Trigger confirms continuation.
8. Stop/target geometry is valid.

### 12.4 CCI interpretation

Continuation confirmation differs from reversal:

- positive/rising CCI or upward zero-line recovery is aligned for bullish continuation;
- an already extreme positive value with falling momentum may indicate exhaustion and should not be treated as maximum confirmation;
- reversal-style “recover from -100” must not be reused blindly.

### 12.5 Regime preference

Prefer:

- breakout expansion;
- trending regime;
- rising ADX;
- non-choppy efficiency ratio;
- expanding Bollinger width.

In strongly ranging/choppy conditions, reject or heavily damp according to policy.

---

## 13. Confidence calculation and arbitration

### 13.1 Do not use an unrestricted additive vote

Do not implement:

```text
Liquidity +20, CCI +15, RSI +10, zone +25; total > 50 means trade
```

Instead each playbook produces typed component quality:

- context quality;
- location quality;
- catalyst quality;
- trigger quality;
- confirmation quality;
- geometry quality.

Mandatory components have pass/fail gates.

### 13.2 Suggested bounded formula

After mandatory gates pass:

```text
Core confidence = minimum of mandatory component qualities
Context adjustment = bounded [-8, +8]
Confirmation adjustment = bounded [-10, +10]
Confluence adjustment = bounded [0, +8]
Final confidence = clamp(Core + adjustments, 0, 100)
```

Document exact weights/options and emit contribution records for audit.

A very strong CCI value must not compensate for a poor liquidity event or invalid zone.

### 13.3 Arbitration

Create:

`Agent/Strategies/StructuralConfluence/StructuralCandidateArbitrator.cs`

Rules:

1. At most one final candidate per agent evaluation.
2. Remove candidates with invalid geometry first.
3. Opposite-direction ready candidates default to `Observe` with `StructuralPlaybookConflict`.
4. Same-direction candidates may be ranked by:
   - mandatory quality floor;
   - geometry quality;
   - confidence;
   - freshest catalyst;
   - stable playbook ID tie-break.
5. Same-direction confluence may add a small bounded adjustment, but preserve the selected primary playbook ID.
6. Never merge stops/targets from different candidates unless a specific deterministic geometry rule defines it.

---

## 14. Agent implementation

Create:

`Agent/Strategies/StructuralConfluence/StructuralConfluenceAgent.cs`

### 14.1 Contract

- `Name = "Structural Confluence"`
- `ExitManagementMode = AgentExitManagementMode.Bracket`
- `TriggerInterval = options.TriggerInterval`
- `RequiredIntervals = options.RequiredIntervals`

### 14.2 Evaluate sequence

```text
Validate context/instrument/time
→ reject duplicate/stale epoch idempotently
→ build StructuralEvidencePacket
→ update/evaluate each enabled playbook in stable ID order
→ arbitrate ready candidates
→ build structural geometry
→ produce Observe or Buy/Sell AgentDecision
```

### 14.3 AgentDecision extensions

Extend `Agent/Models/AgentModels.cs` with fields required for audit and ML:

```csharp
public string? PlaybookId { get; init; }
public string? PlaybookVersion { get; init; }
public string? StructuralSetupId { get; init; }
public StructuralSetupLifecycle? StructuralLifecycle { get; init; }
public decimal? ContextQuality { get; init; }
public decimal? LocationQuality { get; init; }
public decimal? CatalystQuality { get; init; }
public decimal? TriggerQuality { get; init; }
public decimal? ConfirmationQuality { get; init; }
public decimal? GeometryQuality { get; init; }
public string? CciConfirmationState { get; init; }
public decimal? EntryCci { get; init; }
public decimal? EntryCciMomentumChange { get; init; }
public string? EntryCciRelationship { get; init; }
public decimal? SweepPenetrationAtr { get; init; }
public decimal? ReclaimStrength { get; init; }
public int? LiquidityPoolTouchCount { get; init; }
public int? SupplyDemandZoneTouchCount { get; init; }
public decimal? SupplyDemandPenetrationRatio { get; init; }
public IReadOnlyList<string> StructuralSetupReasonCodes { get; init; } = [];
```

Keep existing structural fields and fill them where relevant:

- `EntrySupplyDemandZoneId`
- `TargetLiquidityPoolId`
- `StructuralInvalidationReference`
- profile hashes;
- management switches/revision.

### 14.4 IDs

Use stable SHA-256-based IDs from:

- strategy version;
- instrument;
- playbook ID;
- primary pool/zone ID;
- catalyst event ID/time;
- direction.

Do not use `Guid.NewGuid()` for deterministic setup/decision identity.

### 14.5 Existing position behaviour

The agent should not emit a new Buy/Sell when the context shows an existing position owned by the same strategy/instrument unless an explicit future pyramiding option is enabled. Initial version: no pyramiding.

Close decisions should remain under TradeManager/bracket behaviour unless the playbook has a clear structural invalidation and the existing architecture expects strategy exits. Since v1 uses `Bracket`, do not add ad hoc close rules that fight TradeManager.

## 14A. Mandatory Simulator Experiment Orchestration Overhaul

The current Simulator page is a capable single-comparison runner, but the structural agent and its ML/calibration variants require a first-class experiment system. Do not implement profile automation as a loop in `SimulatorPanel.vue`, and do not submit a collection of unrelated `/api/simulations` requests from the browser. The server must own the experiment, its bounded concurrency, its training/evaluation boundary, artifact freezing, cancellation, resume state, and aggregation.

### 14A.1 Existing partial implementation to retain and correct

The repository already contains useful pieces:

- `QuantResearch.Training/Pipeline/PreRunCalibrationPlanner.cs`
  - defaults to two calendar months of learning and a ten-day pre-evaluation gap;
- `QuantResearch.Training/Pipeline/PreRunCalibrationService.cs`
  - trains setup → meta-model → management;
  - runs independent strategy chains with `Task.WhenAll`;
  - removes existing calibration policies from the raw learning backtests;
- `DashboardLive/SimulationApi.cs`
  - can auto-train and inject the resulting artifacts into the evaluation request;
- `Simulator/Services/BacktestApplicationService.cs`
  - owns a bounded job queue and multiple workers;
- `DashboardLive/Program.cs`
  - currently configures three concurrent backtest jobs;
- `StreamingComparativeEngine`
  - already supports parallel strategy workers, shared immutable analysis, per-assignment analysis overrides, and deterministic barriers;
- `QuantResearchRunner/Experiments/ExperimentLedger.cs`
  - already provides resumable experiment-ledger concepts;
- `AnalysisProfileRegistry` and `StrategyFactoryEntry.AnalysisOptionsOverride`
  - already provide the beginnings of analysis-profile reuse/isolation.

Do not discard these. Build the experiment orchestration layer around them.

Correct these current limitations:

1. `POST /api/simulations` currently awaits `PreRunCalibrationService.TrainAsync` before returning `202 Accepted`. A long learning run therefore blocks the HTTP request and is not represented as a cancellable/pausable job phase.
2. A request still represents one simulation comparison, not a parent experiment containing multiple profile runs.
3. Profile identity is not first-class; multiple configurations of the same strategy cannot be addressed reliably by stable profile/run IDs.
4. Concurrency exists at several levels but has no single resource governor, creating a nested-parallelism/oversubscription risk.
5. Dashboard and CLI auto-training have different semantics. Dashboard auto-calibration uses the fresh artifacts in the same held-out evaluation; `BacktestRunner --auto-train-calibration` deliberately creates a pending-review candidate that is not used by the run.
6. Training, analysis warm-up, external embargo, evaluation warm-up, and held-out evaluation are not represented as one explicit timeline object.
7. `PreRunCalibrationService` merges artifacts across requested strategy chains. Experiment profiles must normally keep their trained bundle isolated by profile revision; two profiles with different playbook/CCI/threshold behaviour must not silently share a merged artifact.

### 14A.2 Explicit four-phase timeline

Introduce a timeline that distinguishes four different concepts:

```text
Training analysis warm-up
    → Learning / fitting window
    → External embargo gap
    → Held-out evaluation window
```

Evaluation itself also needs causal analysis warm-up before entries are enabled:

```text
Evaluation analysis warm-up (may consume embargo/pre-evaluation candles)
    → EvaluationFrom: entries and scoring enabled
```

The internal representation must use UTC half-open ranges `[from, to)` to remove inclusive-date ambiguity.

Create under `Simulator/Experiments/Models` or a shared `QuantResearch` model namespace:

```csharp
public sealed record SimulationExperimentTimeline
{
    public required DateTimeOffset LearningFrom { get; init; }
    public required DateTimeOffset LearningTo { get; init; }       // exclusive
    public required DateTimeOffset EvaluationFrom { get; init; }
    public required DateTimeOffset EvaluationTo { get; init; }     // exclusive

    public int TrainingWarmupDays { get; init; } = 21;
    public int EvaluationWarmupDays { get; init; } = 21;
    public int EmbargoDays { get; init; } = 10;

    public DateTimeOffset ResolveTrainingStreamFrom();
    public DateTimeOffset ResolveEvaluationStreamFrom();
    public void Validate();
}
```

Validation invariants:

```text
LearningFrom < LearningTo
LearningTo <= EvaluationFrom
EvaluationFrom < EvaluationTo
EvaluationFrom - LearningTo >= EmbargoDays
TrainingWarmupDays >= 0
EvaluationWarmupDays >= 0
```

Support two UI/input modes:

1. **Explicit dates** — user supplies `LearningFrom`, `LearningTo`, `EvaluationFrom`, and `EvaluationTo`.
2. **Relative learning window** — user supplies learning months/days and embargo days; server resolves exact dates from `EvaluationFrom` and persists the resolved timeline.

The user-facing example should appear as a visual timeline, such as:

```text
Training data stream starts: 2026-03-11  (21d indicator/annotator warm-up)
Learning entries/labels:      2026-04-01 → 2026-05-21 exclusive
Embargo/no fitting:           2026-05-21 → 2026-06-01
Held-out evaluation:          2026-06-01 → 2026-07-01
```

The UI must show exact resolved boundaries. Do not claim “10 days” when the selected explicit dates produce a different number of full days.

Do not rely only on a manually entered warm-up day count. Add an `AnalysisWarmupPlanner` that calculates a conservative minimum from the resolved profile:

- every agent `RequiredIntervals`;
- the slowest required timeframe;
- indicator periods and analysis-history capacities;
- `PriceActionOptions.CalibrationLookback` (currently a significant 250-bar requirement);
- swing left/right confirmation;
- regime persistence/confirmation windows;
- supply/demand, liquidity, NeoWave, channel, and value-reference lookbacks;
- trade-management structure intervals.

The effective warm-up is:

```text
max(user minimum warm-up, required-bars-derived warm-up, WarmupBaseCandleCount-derived warm-up)
```

Integrate with, rather than delete, `BacktestRuntimeOptions.ResolveWarmupFrom`. Persist both the requested and resolved warm-up plus the dominant requirement/reason. Fail validation when the available dataset cannot satisfy the calculated minimum unless the user explicitly chooses a diagnostic `AllowInsufficientWarmup` mode; such runs must be prominently marked and excluded from promotion-quality research.

### 14A.3 Leakage rules across the timeline

The following rules are mandatory:

1. Training warm-up candles may initialize indicators, swings, zones, liquidity, NeoWave, regime, and playbook state, but cannot create training samples or scored trades before `LearningFrom`.
2. Only trades whose candidate/open time is at or after `LearningFrom` are eligible for training.
3. A trade whose label/close time is at or after `LearningTo` must be excluded or purged from the fitting set. Never derive a label using a candle in the external embargo or evaluation range.
4. Internal folds in `CalibrationTrainingPipeline` continue to use `PurgedTimeSeriesCrossValidator` and their own fold embargo. The external learning-to-evaluation embargo is an additional boundary, not a replacement.
5. The calibration bundle must be fully created, validated, content-hashed, and frozen before evaluation processes its first tradable candle.
6. Evaluation results must never update the model used by that same experiment run.
7. Evaluation analysis warm-up can causally consume market candles immediately before `EvaluationFrom`, including candles in the external embargo. Those candles may update indicators/annotation/playbook observation state, but they must not update the frozen model or its training statistics.
8. Evaluation starts with no open positions. A setup that began during evaluation warm-up may remain active only when the playbook says it is still causally valid; record `SetupOriginatedDuringWarmup=true` for diagnostics.
9. Training and evaluation use separate mutable engine/agent/session instances. Only immutable market data/cache entries and the frozen artifact bundle may be shared.
10. Every result must persist the complete resolved timeline and artifact IDs/hashes.

### 14A.4 First-class strategy profiles

Add a reusable, versioned simulation profile model. Do not identify a profile only by strategy type.

Suggested model:

```csharp
public sealed record SimulationStrategyProfile
{
    public required Guid ProfileId { get; init; }
    public required int Revision { get; init; }
    public required string Name { get; init; }
    public required TradingAgentDefinition Agent { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public ChartAnnotationOptions Analysis { get; init; } = new();
    public BacktestRuntimeProfile Runtime { get; init; } = new();
    public PositionManagementOptions Management { get; init; } = new();
    public CalibrationExperimentPolicy Calibration { get; init; } = new();
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required string ContentHash { get; init; }
}
```

Use a stable profile hash over all behaviour-affecting fields. At experiment launch, snapshot every selected profile into the experiment manifest. Later edits to a saved profile must not mutate an already-running or completed experiment.

Add an `ISimulationStrategyProfileStore` with a file-backed implementation under `.cache/simulation-profiles` initially. Keep the abstraction suitable for later PostgreSQL migration, but database migration is not part of this pass.

Required profile operations:

- create;
- clone;
- update as a new revision;
- list;
- read;
- archive;
- compare/diff resolved JSON;
- validate through the shared trading-agent catalog.

### 14A.5 Profile variants and matrix generation

The UI and plan schema should allow the user to create a small explicit matrix without duplicating agent classes. For the first structural experiment, support variants such as:

```text
sweep-base
sweep-cci-soft
sweep-cci-required
sweep-sd-preferred
sweep-sd-required
sweep-cci-soft-sd-preferred
pullback-base
pullback-cci-soft
break-retest-base
break-retest-cci-soft
```

Implement variants as immutable profile snapshots or a base profile plus validated override patches. Persist the fully resolved profile for every child run; never rely on applying the patch again during replay.

Each experiment child needs a unique `ProfileRunId`, even when several runs use the same `TradingAgentKind`:

```csharp
public sealed record SimulationExperimentProfileRun
{
    public required Guid ProfileRunId { get; init; }
    public required SimulationStrategyProfileSnapshot Profile { get; init; }
    public string? BaselineProfileRunId { get; init; }
}
```

### 14A.6 Parent experiment job

Create a new service rather than turning `BacktestApplicationService` into an unbounded god object:

```text
Simulator/Experiments/ISimulationExperimentApplicationService.cs
Simulator/Experiments/SimulationExperimentApplicationService.cs
Simulator/Experiments/Models/SimulationExperimentRequest.cs
Simulator/Experiments/Models/SimulationExperimentSnapshot.cs
Simulator/Experiments/Persistence/ISimulationExperimentRepository.cs
Simulator/Experiments/Persistence/FileSimulationExperimentRepository.cs
```

The parent experiment owns these stages:

```text
Queued
→ ResolvingProfiles
→ PreparingDatasets
→ TrainingWarmup
→ Learning
→ FreezingArtifacts
→ EmbargoReady
→ EvaluationWarmup
→ Evaluating
→ Aggregating
→ Completed / Failed / Cancelled
```

Training and evaluation may consist of multiple child jobs. The parent snapshot must expose:

- timeline;
- selected profile snapshots;
- child learning/evaluation job IDs;
- per-profile stage and progress;
- artifact bundle IDs and hashes;
- shared dataset/cache status;
- resource permits in use;
- warnings and failure reasons;
- comparison summary when completed.

Parent pause/cancel must cascade to all owned child jobs. Process restart must mark interrupted parents consistently and allow an explicit resume operation when all required manifests/artifacts/data are intact.

### 14A.7 API design

Keep existing `/api/simulations` for backward-compatible one-off runs.

Add:

```text
POST   /api/simulation-profiles
GET    /api/simulation-profiles
GET    /api/simulation-profiles/{id}/revisions/{revision}
POST   /api/simulation-profiles/{id}/clone
POST   /api/simulation-profiles/{id}/revisions
DELETE /api/simulation-profiles/{id}                 (archive semantics)

POST   /api/simulation-experiments
GET    /api/simulation-experiments
GET    /api/simulation-experiments/{id}
POST   /api/simulation-experiments/{id}/pause
POST   /api/simulation-experiments/{id}/resume
POST   /api/simulation-experiments/{id}/cancel
GET    /api/simulation-experiments/{id}/results
GET    /api/simulation-experiments/{id}/manifest
```

Move these endpoints into new files:

```text
DashboardLive/SimulationProfileApi.cs
DashboardLive/SimulationExperimentApi.cs
```

Do not keep expanding the already-large `DashboardLive/SimulationApi.cs`.

The POST endpoint must validate and enqueue quickly, then return `202 Accepted`. It must not synchronously wait for model learning.

### 14A.8 Training artifact policy

Define explicit modes:

```csharp
public enum ExperimentCalibrationMode
{
    Disabled,
    ReuseSpecifiedArtifacts,
    TrainFreshAndUseForHeldOutEvaluation,
    TrainFreshPendingReviewOnly
}
```

For automated strategy experiments, the normal mode is `TrainFreshAndUseForHeldOutEvaluation`. These artifacts are **experiment-scoped research artifacts**, not approved live/demo policies.

For every profile, key the learning bundle by at least:

```text
Profile content hash
Agent/strategy version
Feature schema version/hash
Instrument set
Learning window
Training warm-up definition
Internal fold/embargo settings
Source data hash
Code/build version when available
```

Do not merge fresh artifacts across profile revisions by default. Reuse is allowed only when the complete training key is identical.

Retain the live-policy approval boundary:

```text
Experiment-scoped artifact
→ reviewed candidate
→ approved TradingPolicyProfile
→ eligible for live Demo activation
```

### 14A.9 Parallel execution and resource governance

Do not simply call `Task.WhenAll` over every profile. Introduce bounded, resource-aware scheduling.

Add a singleton service such as:

```csharp
public interface ISimulationResourceGovernor
{
    ValueTask<IAsyncDisposable> AcquireExperimentAsync(...);
    ValueTask<IAsyncDisposable> AcquireProfileGroupAsync(...);
    ValueTask<IAsyncDisposable> AcquireHistoricalDownloadAsync(...);
}
```

At minimum govern:

- maximum concurrent parent experiments;
- maximum concurrent profile/analysis groups;
- maximum historical downloads per broker;
- maximum total strategy workers;
- optional estimated-memory budget;
- cancellation while waiting for a permit.

Avoid nested oversubscription. For example, four profile groups each starting four strategy workers must not silently create sixteen CPU-heavy workers when the configured global budget is eight.

Persist the resolved parallelism plan in the experiment manifest.

### 14A.10 Share data and analysis safely

Build compatibility keys:

```text
HistoricalDataCompatibilityKey
    broker/source + environment + instrument set + execution interval + date range + price component

AnalysisCompatibilityKey
    historical-data key + analysis intervals + full ChartAnnotationOptions hash + currency-strength universe

AgentExecutionCompatibilityKey
    analysis key + account mode + execution/fill/financing semantics
```

Scheduling rules:

1. Profiles with the same historical-data key reuse the same immutable cached dataset/manifest; do not redownload it.
2. Profiles with the same analysis key should run in the same `StreamingComparativeEngine` group where practical and use existing parallel strategy workers/shared immutable snapshots.
3. Profiles differing only in playbook thresholds, CCI mode, calibration artifact, or confidence policy should usually share analysis.
4. Profiles with different annotation detector settings or incompatible timeframe sets must use separate analysis-profile groups, bounded by the resource governor.
5. Mutable agent, playbook, portfolio, execution, and trade-management state is always isolated per profile run.
6. Parallel and sequential modes must produce byte-equivalent deterministic result records after stable sorting.

Use the existing `AnalysisProfileRegistry`, `AnalysisOptionsOverride`, and `AnalysisSharingMode.SharedImmutableSnapshots` rather than recomputing every annotation stream independently.

### 14A.11 Dataset preparation

Add a dataset manifest/lease abstraction around historical candle preparation so training and evaluation children can reference the same verified source data without sharing mutable stream cursors:

```csharp
public sealed record HistoricalDatasetManifest
{
    public required string DatasetId { get; init; }
    public required string DataHash { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required IReadOnlyList<InstrumentKey> Instruments { get; init; }
    public required BarInterval ExecutionInterval { get; init; }
    public required string SourceIdentity { get; init; }
}
```

The widest required stream begins at the earliest training warm-up boundary and ends at `EvaluationTo`. Child engines receive independent readers over the immutable cache/dataset.

### 14A.12 Comparison and statistical output

The parent experiment must aggregate results by profile and provide paired comparisons against an optional baseline profile:

- net profit and net R;
- expectancy;
- maximum drawdown;
- profit factor;
- trade count;
- win/loss distribution;
- MFE/MAE;
- playbook/regime/CCI breakdown;
- setup-calibration reliability and Brier score;
- meta-model accept/reject/reduce counts;
- runtime and candles/second;
- difference versus baseline on the same evaluation dates;
- warning when sample sizes are insufficient.

Do not rank only by net profit or win rate. The default leaderboard should use a configurable objective with drawdown/sample constraints, aligned with existing QuantResearch selection concepts.

### 14A.13 CLI parity

The same application service must be callable from CLI. Add either:

```text
BacktestRunner experiment --plan experiment.json
```

or a clearly documented command in `QuantResearchRunner` that uses `SimulationExperimentApplicationService`. Do not implement a second orchestration algorithm in the CLI.

Suggested plan shape:

```json
{
  "name": "structural-sweep-cci-ablation",
  "timeline": {
    "learningFrom": "2026-04-01T00:00:00Z",
    "learningTo": "2026-05-21T00:00:00Z",
    "embargoDays": 10,
    "evaluationFrom": "2026-06-01T00:00:00Z",
    "evaluationTo": "2026-07-01T00:00:00Z",
    "trainingWarmupDays": 21,
    "evaluationWarmupDays": 21
  },
  "profiles": [
    { "profileId": "...", "revision": 1 },
    { "profileId": "...", "revision": 2 }
  ],
  "calibrationMode": "TrainFreshAndUseForHeldOutEvaluation",
  "parallelism": {
    "maxProfileGroups": 2,
    "maxTotalStrategyWorkers": 8,
    "maxHistoricalDownloads": 1
  }
}
```

Add resume support using the existing `ExperimentLedger` concepts. A resumed experiment must not retrain a completed profile bundle or rerun a completed held-out evaluation unless the user explicitly requests a clean rerun.

### 14A.14 Dashboard major refactor

`Dashboard/src/components/SimulatorPanel.vue` is already very large and should not absorb another experiment builder. Preserve a **Single Run** mode for quick debugging and add a separate **Experiment** mode/page.

Refactor toward components such as:

```text
Dashboard/src/components/simulator/SingleSimulationForm.vue
Dashboard/src/components/simulator/experiment/ExperimentTimelineEditor.vue
Dashboard/src/components/simulator/experiment/StrategyProfileTable.vue
Dashboard/src/components/simulator/experiment/ProfileVariantBuilder.vue
Dashboard/src/components/simulator/experiment/CalibrationPlanEditor.vue
Dashboard/src/components/simulator/experiment/ExperimentResourceControls.vue
Dashboard/src/components/simulator/experiment/ExperimentReview.vue
Dashboard/src/components/simulator/experiment/ExperimentMonitor.vue
Dashboard/src/components/simulator/experiment/ExperimentComparison.vue
Dashboard/src/composables/useSimulationExperiment.ts
Dashboard/src/composables/useSimulationProfiles.ts
Dashboard/src/types/simulation-experiments.ts
```

Suggested workflow:

```text
1. Dataset and timeline
2. Profiles and variants
3. Learning/calibration policy
4. Parallelism/resources
5. Review resolved plan
6. Launch and monitor
7. Compare results
```

The timeline editor must visually show:

- training warm-up;
- learning window;
- embargo;
- evaluation warm-up;
- held-out evaluation;
- exact UTC dates and durations;
- a clear warning for overlap/leakage.

The monitor must show parent and child progress without flooding the browser with all replay frames. Continue using SignalR/coalesced snapshots for progress and fetch detailed replay/results on demand.

### 14A.15 Backward compatibility

- Existing `POST /api/simulations` and quick Simulator flow continue to work.
- Existing legacy/improved single-run results remain unchanged by default.
- Current `AutoCalibrateBeforeRun` request fields may be mapped into a one-profile experiment internally in a later migration, but do not silently change their semantics during the first refactor.
- Mark inconsistent legacy CLI auto-train behaviour clearly. Prefer new explicit names over silently reinterpreting `--auto-train-calibration`.
- Persist schema versions for experiment requests, profile snapshots, parent snapshots, dataset manifests, and result aggregates.


---

## 15. Simulator integration

This section applies to both the backward-compatible single-run path and child evaluation runs created by the mandatory experiment orchestrator in §14A. Do not implement the structural agent only in the legacy single-run request.

### 15.1 Request model

Update `Simulator/Models/BacktestConfiguration.cs`:

- allow `structural-confluence` in `BacktestRequest.Strategies` and `StrategyAssignments`;
- replace literal validation with `TradingAgentTypeIds.Parse`/catalog lookup;
- add `StructuralConfluenceStrategyOptions` to `BacktestRuntimeOptions` or a dedicated agent-options map;
- avoid adding dozens of unrelated top-level request fields if a nested DTO can be used.

Preferred request shape:

```csharp
public StructuralConfluenceStrategyOptions StructuralConfluence { get; init; } = new();
```

Assignment override should support the generic agent definition rather than only `ProgressiveStrategyOptions? AgentOptionsOverride`.

Migration-safe approach:

```csharp
public TradingAgentDefinition? AgentDefinitionOverride { get; init; }
public ProgressiveStrategyOptions? AgentOptionsOverride { get; init; } // legacy alias
```

Normalize once during request validation.

### 15.2 Backtest application service

Update `Simulator/Services/BacktestApplicationService.cs`:

- use shared `TradingAgentFactory`;
- map canonical strategy type to a `TradingAgentDefinition`;
- remove `legacy/improved` switch blocks at current construction points;
- include structural options in simulation configuration identity/hash;
- ensure analysis options enable:
  - supply/demand;
  - liquidity;
  - S/D-liquidity confluence;
  - CCI analysis;
  - price-action setups;
  - required regime/NeoWave settings.

If structural strategy is requested while required detector profiles are disabled, fail validation rather than silently producing zero trades.

### 15.3 Strategy session and trade records

Update `Simulator/Models/SimulationModels.cs` `SimulatedTradeRecord` with entry fields corresponding to the new decision fields:

- playbook ID/version;
- structural setup ID;
- component qualities;
- CCI state/value/change/relationship;
- sweep penetration/reclaim;
- pool and zone touch/freshness/penetration metrics;
- structural reason codes.

Populate in `Simulator/Engine/StrategySimulationSession.cs` at trade creation and preserve through close/update copies.

### 15.4 Rejected candidate diagnostics

The simulator must record structurally valid candidates rejected by:

- trading conditions;
- setup calibration;
- meta-labeling;
- portfolio admission;
- geometry/R:R;
- playbook conflict.

Do not train outcomes from rejected candidates unless a counterfactual shadow variant actually paper-trades them. For unbiased comparison, create explicit simulator variants with calibration/meta disabled rather than pretending a rejected candidate was executed.

### 15.5 Replay

Extend replay contracts/builders so chart/replay can show:

- selected playbook;
- lifecycle transition;
- pool/zone used;
- sweep/accepted-break/retest marker;
- trigger marker;
- CCI confirmation state;
- stop and target sources;
- rejection reason.

Keep payloads bounded, following the existing live SSE projector design.

---

## 16. Dashboard Simulator integration

Implement these structural-agent controls in the refactored Single Run and Experiment profile editors described in §14A.14. Do not continue growing one monolithic `SimulatorPanel.vue`.

Update:

- `Dashboard/src/components/SimulatorPanel.vue`
- `Dashboard/src/types.ts`
- `DashboardLive/SimulationApi.cs`
- relevant API request/response contracts.

### 16.1 Strategy selection

Add `Structural Confluence` to selectors and assignment rows.

Default existing preset behaviour should remain unchanged unless a new structural preset is selected.

Add a dedicated preset:

```text
Structural shadow research
```

Suggested preset:

- strategy: `structural-confluence`;
- independent strategy account;
- supply/demand enabled;
- liquidity enabled;
- confluence enabled;
- price action required;
- CCI soft;
- setup/meta calibration selectable;
- 2h context / 15m setup / 5m trigger;
- detailed excursion tracking enabled for research runs.

### 16.2 Form section

Provide compact grouped controls:

- playbook enable toggles;
- context/setup/trigger intervals;
- CCI mode per playbook;
- S/D confluence required/preferred;
- minimum pool/zone quality;
- maximum touch count;
- sweep penetration ATR range;
- trigger window;
- minimum R:R;
- calibration/meta-model artifact selection.

Do not duplicate every low-level detector profile setting in the initial UI. Advanced detector settings may remain server/config defaults.

### 16.3 Results

In strategy comparison and trade details, show:

- playbook;
- number of candidate setups;
- number rejected by each gate;
- trades;
- expectancy;
- net R;
- drawdown;
- MFE/MAE;
- CCI confirmation breakdown;
- zone/liquidity confluence breakdown.

### 16.4 Research panel

Update `Dashboard/src/components/ResearchPanel.vue` so the strategy selector includes `structural-confluence` and no comment/API validation claims only legacy/improved.

---

## 17. CLI integration

Update:

- `BacktestRunner/BacktestCommandOptions.cs`
- `BacktestRunner/Program.cs`

### 17.1 Strategy parsing

Support:

```bash
--strategies structural-confluence
```

and mixed comparisons:

```bash
--strategies legacy,improved,structural-confluence
```

Use canonical catalog parsing, not substring inference.

### 17.2 Options

Add clear structural flags or a JSON policy-file option. Avoid a flat explosion where possible.

Minimum CLI flags:

```text
--structural-context-interval
--structural-setup-interval
--structural-trigger-interval
--structural-playbooks sweep,pullback,break-retest
--structural-sweep-cci-mode disabled|soft|required
--structural-pullback-cci-mode disabled|soft|required
--structural-break-retest-cci-mode disabled|soft|required
--structural-sd-confluence preferred|required|disabled
```

Include these options in help output and configuration identity.

### 17.3 Reproducibility

Print resolved agent definition and policy hash at run start. Persist exact resolved options in simulation metadata.

---

## 18. Research and calibration integration

### 18.1 Strategy recognition

Update:

- `QuantResearch.Training/Pipeline/PreRunCalibrationService.cs`
- `QuantResearch.Training/Pipeline/LiveCalibrationTrainingScheduler.cs`
- `QuantResearch.Training/Pipeline/CalibrationArtifactMerger.cs`
- `DashboardLive/ResearchCalibrationService.cs`
- `DashboardLive/ResearchContracts.cs`
- `QuantResearchRunner` command parsing.

Use catalog parsing and support structural strategy IDs.

### 18.2 Playbook dimension

Add `PlaybookId` to:

- `SimulatedTradeRecord`;
- `QuantResearch.Models.ResearchTrade`;
- `SetupOutcome`;
- `ResearchTradeMapper`;
- setup calibration buckets;
- meta-model calibration buckets.

Calibration must not merge all playbooks into one cohort.

Suggested setup cohort key:

```text
StrategyId + PlaybookId + InstrumentGroup + Regime + ConfidenceBucket
```

### 18.3 Meta-label feature schema v2

Extend `RiskManager/Calibration/MetaLabelFeatures` with structural fields:

```csharp
public string? PlaybookId { get; init; }
public string? CciConfirmationState { get; init; }
public decimal? Cci { get; init; }
public decimal? CciMomentumChange { get; init; }
public string? CciRelationship { get; init; }
public decimal? LiquidityPoolQuality { get; init; }
public decimal? SweepPenetrationAtr { get; init; }
public decimal? ReclaimStrength { get; init; }
public int? LiquidityPoolTouchCount { get; init; }
public decimal? SupplyDemandZoneQuality { get; init; }
public int? SupplyDemandZoneTouchCount { get; init; }
public decimal? SupplyDemandPenetrationRatio { get; init; }
public bool? SupplyDemandLiquidityConfluence { get; init; }
public decimal? DistanceToNearestTargetAtr { get; init; }
public decimal? DistanceToInvalidationAtr { get; init; }
```

Bump:

```csharp
MetaLabelFeatureFactory.SchemaVersion = "tradinghub-meta-v2";
```

Old v1 artifacts must fail clearly and require retraining. Do not silently treat them as v2.

Because this repository is currently a development system rather than a certified production deployment, an explicit schema bump and retraining is safer than pretending compatibility.

### 18.4 Feature creation

Update `MetaLabelFeatureFactory.Create` to populate v2 from `AgentDecision` and causal snapshots.

Do not recalculate structural detection inside `RiskManager`. The agent decision must carry selected structural feature values.

Correct the existing schema-validation inconsistency while doing this work:

- `QuantResearch.Training/Pipeline/CalibrationTrainingPipeline.cs` writes setup artifacts with `FeatureSchemaHash = MetaLabelFeatureFactory.SchemaVersion`;
- `TradingCore/Pipeline/StrategyDecisionPipelineFactory.cs` correctly validates setup artifacts against `MetaLabelFeatureFactory.SchemaVersion`;
- `LiveTradingHost/LiveEngineHostedService.cs` currently calls `resolved.SetupCalibration.Validate(resolved.Bundle.FeatureSchemaHash)`, where `Bundle.FeatureSchemaHash` is the `RuntimeFeaturePolicy.ComputeHash()` option-content hash.

The live path must validate against the same feature-vector schema identifier as simulator/research (`MetaLabelFeatureFactory.SchemaVersion`), not the policy option-content hash. Keep the policy content hash for provenance/configuration identity, but do not confuse it with the feature schema. Add a live/simulator parity test for this invariant.

### 18.5 Calibrated bucket model

Extend `MetaModelBucket` with:

- `PlaybookId`;
- optional coarse `CciState`;
- optional coarse `StructuralConfluenceState`.

Avoid excessive sparse dimensions.

Recommended hierarchical lookup in `CalibratedSetupMetaModel`:

1. exact strategy + playbook + regime + confidence + alignment + CCI/confluence bucket;
2. fallback strategy + playbook + regime + confidence + alignment;
3. fallback strategy + playbook + regime + confidence;
4. no reliable bucket → neutral.

Every bucket must satisfy `MinimumSamples`.

Record the selected fallback level in `ReasonCode`/diagnostics.

### 18.6 No external ML framework in this pass

Do not add ML.NET, LightGBM, TorchSharp, TensorFlow, or neural networks in this implementation pass.

The current deterministic cohort model should first be extended and validated. A trained gradient-boosted model may be a separate future phase after enough shadow samples exist.

### 18.7 Ablation variants

QuantResearch must be able to run at least:

```text
sweep-base
sweep-cci-soft
sweep-cci-required
sweep-sd-confluence
sweep-cci-sd-confluence
pullback-base
pullback-cci-soft
break-retest-base
break-retest-cci-soft
```

Implement variants as policy configurations, not duplicated agent classes.

### 18.8 Evaluation

All automated comparisons must use the frozen artifact learned before the external embargo and the identical held-out evaluation window described in §14A. Report by playbook, instrument, timeframe set, regime, and confirmation state:

- samples;
- trades;
- win rate;
- expected R;
- average R;
- profit factor;
- maximum drawdown;
- MFE and MAE;
- Brier score;
- calibration reliability;
- spread sensitivity;
- walk-forward stability.

---

## 19. True live shadow outcome service

### 19.1 Preserve current candidate recording

Keep existing behaviour in `LiveTradingRuntimeCoordinator` that records non-executing candidates.

Add paper evaluation in addition to—not instead of—the safe non-executing portfolio decision.

### 19.2 Add a shadow outcome component

Create under:

`LiveTrading/Shadow/Outcomes`

Suggested types:

- `ILiveShadowOutcomeService`
- `LiveShadowOutcomeService`
- `ShadowCandidateRecord`
- `ShadowPaperPosition`
- `ShadowFillRecord`
- `ShadowManagementRecord`
- `ShadowOutcomeRecord`
- `LiveShadowOutcomeOptions`

The service must not reference broker write-capable APIs.

### 19.3 Candidate admission

For `StrategyActivationMode.Shadow`:

1. receive the validated pipeline candidate;
2. optionally apply paper portfolio limits in a separate shadow account;
3. derive virtual entry from live executable bid/ask, not midpoint;
4. apply configured slippage/cost assumptions;
5. open a paper position if fill conditions are met;
6. persist candidate and fill atomically through existing live persistence abstraction.

`ObserveOnly` should record the decision but not open a paper position. This preserves a meaningful distinction:

```text
ObserveOnly = signal diagnostics only
Shadow = paper trading and outcomes
```

### 19.4 Paper position updates

Feed the service:

- completed execution candles from the existing market-data coordinator;
- current quotes for spread/marking where available;
- current causal analysis snapshots for management.

Evaluate:

- stop/target order;
- conservative same-bar ambiguity policy;
- gap/slippage behaviour;
- partial exits;
- stop amendments;
- TradeManager recommendations;
- structural invalidation where configured;
- financing/costs if live paper duration crosses financing boundaries.

Avoid referencing the `Simulator` project from `LiveTrading`.

Where reusable logic is needed, extract environment-neutral paper execution primitives into a new small project, for example:

```text
PaperTrading
```

Both `Simulator` and `LiveTrading` may reference it, but `LiveTrading` must not reference `Simulator`.

Do not perform a broad simulator rewrite merely to create this project. Extract only stable pure models/policies needed for parity.

### 19.5 Persistence

Use the existing `ILiveTradingPersistence` / `FileLiveTradingPersistence` path initially.

Persist streams such as:

```text
shadow-candidates
shadow-paper-fills
shadow-paper-positions
shadow-management
shadow-outcomes
```

Include:

- exact `AgentInstanceKey`;
- policy bundle and revision;
- analysis profile hash;
- strategy and playbook;
- candidate feature snapshot;
- setup/meta decisions;
- paper execution assumptions;
- entry/exit/MFE/MAE/net R;
- close reason.

Do not require the new PostgreSQL module to be wired into live runtime as part of this agent task. Keep persistence abstraction-friendly so DB migration can happen later.

### 19.6 Recovery

On host restart:

- restore open paper positions from checkpoint/journal;
- replay shadow events idempotently;
- avoid duplicate virtual entries using candidate/decision ID;
- mark irrecoverably ambiguous paper positions with an explicit terminal diagnostic rather than guessing.

### 19.7 Status API and dashboard

Extend live status DTO/API and `Dashboard/src/components/LiveDemoPanel.vue` with:

- shadow candidate count;
- open paper positions;
- completed paper trades;
- net shadow R/P&L;
- last playbook/candidate;
- last rejection;
- current paper stop/target;
- policy revision.

Do not mix shadow paper balances with the real broker account balance.

---

## 20. Live host integration

### 20.1 Construction

Update `LiveTrading/Agents/LiveAgentFactory.cs` to accept `TradingAgentDefinition` instead of `ProgressiveAgentKind + ProgressiveStrategyOptions`.

Validation must ensure structural agent options and analysis profile agree:

- supply/demand detector enabled when any S/D playbook is enabled;
- liquidity detector enabled when any liquidity playbook is enabled;
- confluence detector enabled when confluence is required/preferred;
- required intervals exist in market definition;
- CCI analysis configuration is present.

### 20.2 Assignment

Example conceptual configuration:

```json
{
  "Instrument": "FX:EUR/USD",
  "ExecutionInterval": "1m",
  "AnalysisBaseInterval": "1m",
  "AnalysisIntervals": ["5m", "15m", "1h"],
  "Strategies": [
    {
      "StrategyId": "structural-confluence",
      "PolicyBundleId": "structural-confluence-eurusd-shadow-v1",
      "Mode": "Shadow",
      "Enabled": true
    }
  ]
}
```

The exact JSON representation must follow existing converters for `BarInterval` and `InstrumentKey`.

### 20.3 Safety

- default mode remains `Shadow`;
- `BrokerWritesEnabled` remains false during validation;
- automatic execution must require an `ApprovedForDemo` profile and existing live safety checks;
- do not add a shortcut that lets structural agent bypass account-wide epoch or portfolio coordination.

---

## 21. Trade management

The new agent is `Bracket` mode and should reuse current `TradeManager` features.

### 21.1 Policy selection

Current policy objects have `LegacyManagement` and `ImprovedManagement`. Add a distinct structural management policy:

```csharp
public PositionManagementOptions StructuralManagement { get; init; }
```

Update:

- `TradingPolicyProfile`
- `LiveTradingPolicyBundle`
- policy configuration hash;
- simulator runtime options;
- `LivePositionManagementService.ResolveOptions`;
- simulator management option resolution;
- dashboard form if exposed.

Do not select structural management through string `Contains("legacy")` fallbacks.

Suggested defaults:

- preserve bracket target;
- break-even after 1R;
- structure/ATR trailing after 1.5–2R;
- scale-out enabled;
- profit floor and maximum giveback enabled;
- structural management references pinned at entry.

### 21.2 Structural references

At entry, pin:

- originating zone ID/profile/hash/boundaries;
- swept or broken pool ID/profile/hash;
- target pool ID;
- invalidation reference;
- playbook ID/version;
- policy revision.

TradeManager may inspect newer analysis but cannot replace these references silently.

---

## 22. API and contract updates

Update all source-generated/AOT JSON contexts or DTO mappings affected by new records.

Search for:

- `JsonSerializable`
- custom converters;
- replay DTO projection;
- status DTO projection;
- persistence serialization contexts.

The project contains AOT-compatible assemblies. Do not leave new polymorphic records dependent on reflection-only serialization where existing code uses source generation.

Add explicit schema/version fields to persisted/replay objects where backward compatibility matters.

---

## 23. Testing requirements

All tests must be deterministic and use completed-candle causal fixtures.

### 23.1 CCI tests

Add to `Simulator.Tests` or a ChartAnnotator-focused test file:

- CCI threshold crossing up/down;
- momentum direction;
- bars since extreme;
- bullish/bearish divergence using confirmed swings;
- no relationship before swing confirmation;
- relationship expiration;
- profile hash changes when CCI analysis option changes.

### 23.2 Liquidity sweep playbook tests

- bullish sell-side sweep + reclaim + bullish trigger produces Buy;
- bearish inverse produces Sell;
- accepted break does not produce reversal;
- missing reclaim rejects;
- pool created after event cannot be used;
- stale sweep expires;
- required demand/supply confluence enforced;
- CCI soft changes confidence but not direction;
- CCI required blocks neutral/conflicting state;
- invalid geometry rejects;
- nearest obstacle controls R:R.

### 23.3 Supply/demand pullback tests

- first touch of fresh demand with aligned context and trigger buys;
- invalidated zone rejects;
- excess touch count rejects;
- deep penetration rejects;
- opposing context veto works;
- indicator confirmation modes work;
- zone stop and target sources are recorded.

### 23.4 Accepted-break retest tests

- accepted break + retest + trigger continues;
- sweep/reclaim is not misclassified as continuation;
- no retest rejects;
- choppy regime veto/soft behaviour;
- CCI continuation semantics differ from reversal semantics.

### 23.5 Arbitration tests

- two same-direction candidates select deterministically;
- opposite-direction ready candidates produce Observe conflict;
- replaying same epoch does not duplicate a candidate;
- sequential and parallel simulations produce identical trades.

### 23.6 Pipeline/ML tests

- structural decision reaches setup calibration;
- playbook is included in calibration cohort;
- meta-model can reject negative-expectancy structural bucket;
- weak bucket reduces risk only;
- no bucket remains neutral;
- risk multiplier never exceeds 1;
- v1 meta artifact is rejected after v2 schema requirement;
- feature factory never observes a future snapshot.

### 23.7 Simulator/API/CLI tests

Update/add:

- `StrategyAssignmentValidationTests`
- `DashboardRequestRoundTripTests`
- `TradingPolicyPromotionTests`
- `PreRunCalibrationTests`
- CLI option parser tests where present.

Verify:

- `structural-confluence` accepted everywhere;
- unknown values rejected consistently;
- agent options survive request round-trip;
- policy promotion preserves exact structural options;
- configuration hash changes with structural behaviour changes.

### 23.8 Live tests

Add under `LiveTrading.Tests`:

- structural profile constructs same agent/options as simulator;
- shadow mode never invokes broker writes;
- ObserveOnly records no paper fill;
- Shadow creates virtual fill/outcome;
- duplicate candidate is idempotent;
- paper position checkpoint recovery;
- candidate policy revision remains pinned;
- live/simulator fixture parity for a deterministic candle sequence;
- status API exposes shadow results without mixing real account values.

### 23.9 Experiment timeline/orchestration tests

Add deterministic tests covering:

- exact UTC half-open boundary resolution;
- explicit dates and relative learning-window planning;
- training warm-up creates no eligible samples before `LearningFrom`;
- trades closing on/after `LearningTo` are purged from fitting;
- external embargo is enforced;
- evaluation warm-up never mutates the frozen calibration bundle;
- no evaluation result is visible to the model used by that run;
- evaluation starts with zero open positions;
- setup state carried from warm-up is flagged and causal;
- two profiles of the same agent receive distinct `ProfileRunId`s and isolated state;
- identical training keys reuse artifacts; different profile hashes do not;
- compatible profiles share data/analysis while incompatible profiles isolate analysis;
- sequential and bounded-parallel experiments yield identical sorted results;
- global worker limits prevent nested oversubscription;
- parent pause/cancel cascades to child jobs;
- interrupted experiment recovery/resume does not repeat completed learning/evaluation stages;
- POST experiment returns accepted before long learning work completes;
- experiment/profile API JSON round-trips through source-generated serializers;
- the example April→May learning, embargo, June→July evaluation timeline is rendered and persisted exactly.

### 23.10 Existing guard tests

Preserve and update:

- `NoOrderPlacementGuardTests`
- deployment-boundary tests;
- AOT smoke tests;
- all existing legacy/improved strategy tests.

---

## 24. Validation workflow

Run from repository root:

```bash
dotnet restore TradingHub.slnx
dotnet build TradingHub.slnx -c Release
dotnet test TradingHub.slnx -c Release --no-build
```

Also run any repository-provided validation script such as:

```bash
./validate.sh
```

Run focused tests during development:

```bash
dotnet test Simulator.Tests/Simulator.Tests.csproj -c Release --filter "Structural|Cci|MetaModel|StrategyAssignment"
dotnet test LiveTrading.Tests/LiveTrading.Tests.csproj -c Release --filter "Shadow|Parity|NoOrderPlacement"
dotnet test QuantResearchRunner.Tests/QuantResearchRunner.Tests.csproj -c Release --filter "MetaModel|Calibration|PreRun"
```

Do not claim success without showing the exact build/test results. If the local environment prevents a test, document the limitation and still leave the repository buildable.

---

## 25. Implementation phases

### Phase 1 — Core analysis and structural agent

- CCI analysis snapshot/state;
- structural options;
- evidence packet;
- three playbooks;
- state store;
- arbitration;
- geometry;
- `StructuralConfluenceAgent`;
- unit tests.

### Phase 2 — Shared agent catalogue and policy compatibility

- generic agent definition/factory/type IDs;
- simulator/CLI/live/research migration;
- policy schema/profile migration;
- exact configuration hashing;
- tests for old and new profile resolution.

### Phase 3 — Experiment domain and leakage-safe timeline

- `SimulationExperimentTimeline`;
- explicit/relative window planner;
- profile store and immutable profile snapshots;
- experiment request/snapshot/repository;
- parent-child job ownership;
- experiment-scoped artifact policy;
- timeline/leakage tests.

### Phase 4 — Resource-aware orchestration and shared data/analysis

- resource governor;
- dataset manifests;
- compatibility keys;
- bounded profile-group execution;
- reuse existing analysis profile registry/shared immutable snapshots;
- pause/cancel/recovery/resume;
- sequential/parallel parity and oversubscription tests.

### Phase 5 — Simulator API and Dashboard overhaul

- preserve Single Run mode;
- add profile APIs and experiment APIs;
- split the monolithic Simulator UI into focused components/composables;
- timeline/profile/learning/resource/review workflow;
- experiment monitor and comparison dashboard;
- structural replay/trade diagnostics;
- API/UI end-to-end tests.

### Phase 6 — CLI experiment parity

- JSON experiment plan;
- same application service as Dashboard;
- resumable ledger;
- exact resolved plan/profile/artifact hashes in output;
- example structural ablation plan.

### Phase 7 — Calibration/meta-label v2

- playbook dimension;
- structural feature fields;
- schema bump;
- hierarchical calibrated bucket lookup;
- per-profile fresh learning bundle;
- ablation variants and held-out evaluation tests.

### Phase 8 — Live shadow paper outcomes

- candidate-to-paper-trade service;
- virtual fills and management;
- persistence/recovery;
- live status/dashboard;
- no-order and parity tests.

### Phase 9 — Validation and documentation

- full build/tests;
- update README/run documentation;
- provide sample single simulation, experiment-plan, CLI, and live-shadow configurations;
- document timeline semantics, artifact approval boundaries, resource limits, known limitations, and promotion criteria.

Codex may implement phases in separate commits, but the repository must not be left with a strategy selectable in one surface and unavailable in another, or an experiment UI that performs client-side orchestration outside the server-owned job model.

---

## 26. Initial recommended defaults

These are safe research defaults, not claimed optimal values:

```text
Context interval: 1h or existing 2h simulator context
Setup interval: 15m
Trigger interval: 5m
Minimum R:R: 1.5
Stop buffer: 0.20 ATR
Target buffer: 0.10 ATR
Trigger window: 12 trigger bars
CCI mode: Soft
Supply/demand confluence: Preferred
Liquidity sweep requires close back inside: true
No pyramiding
All live deployments: Shadow
Meta-model: enabled only with a compatible v2 artifact
```

Do not hard-code these outside options.

---

## 27. Promotion criteria

The implementation is complete when the feature works technically. The strategy must remain shadow/research until evidence supports promotion.

Do not promote based only on win rate.

Require:

- positive out-of-sample expected R after costs;
- acceptable maximum drawdown;
- walk-forward stability;
- no single-instrument dependence;
- reasonable sample size by playbook;
- calibrated confidence/Brier performance;
- live-shadow behaviour close to simulator assumptions;
- no unexplained missed/duplicate setups;
- safety and persistence soak tests;
- explicit human approval into `ApprovedForDemo`.

Promotion path:

```text
Simulator research
→ held-out walk-forward
→ continuous live Shadow
→ ManualApproval Demo
→ limited Automatic Demo
```

No real-money live activation is part of this blueprint.

---

## 28. Explicit non-goals

Do not include these in this implementation pass:

- neural networks;
- raw-candle end-to-end prediction;
- an ML model that selects direction independently;
- broker order-book liquidity claims;
- live real-money activation;
- PostgreSQL migration of all runtime persistence;
- pyramiding;
- multi-leg options strategies;
- copying or zipping the project.

---

## 29. Required final Codex report

At completion, provide a concise repository report containing:

1. files added/changed;
2. architectural decisions;
3. exact strategy/playbook IDs;
4. simulator usage example;
5. CLI usage example;
6. live shadow configuration example;
7. meta-label schema/version changes;
8. backward-compatibility notes;
9. build and test commands/results;
10. known limitations or deferred work.

Do not generate a zip archive.

---

## 30. Final design summary

The intended authority chain is:

```text
Structural detectors discover context, location, and catalyst
Price action confirms timing
CCI and other indicators confirm or conflict
Structural geometry defines invalidation and realistic targets
The deterministic playbook creates the candidate
Setup calibration and meta-labeling may reject or reduce risk
Risk/portfolio/execution remain final authority
Live Shadow paper-trades the approved candidate without broker writes

Simulator experiments learn on a causally warmed historical window
Freeze profile-specific artifacts before an external embargo
Evaluate multiple immutable profiles on the same held-out window
Share compatible data/analysis with bounded server-owned parallelism
```

This separation must remain visible in source types, reason codes, persisted diagnostics, replay, tests, and dashboard output.
