# TradingHub Supply/Demand and Price-Inferred Liquidity — Codex Implementation Blueprint

## Role

Act as a principal quantitative trading-systems architect, senior .NET engineer, time-series validation specialist, and adversarial reviewer.

Implement two complementary shared-analysis subsystems in the latest TradingHub source:

1. **Price-derived supply and demand zones**
2. **Price-inferred liquidity pools, sweeps, and accepted breakouts**

They must be deterministic, causal, immutable after publication, reusable by multiple Agents, environment-neutral, observable, and safe for simulator/live parity.

Audit the actual code first. Do not trust prior reports without tracing runtime paths.

---

# 1. Target architecture

```text
Completed candles
    ↓
Existing timeframe aggregation
    ↓
Existing confirmed swings / structure
    ↓
SupplyDemandAnalyzer
    ↓
LiquidityPoolAnalyzer
    ↓
LiquidityEventDetector
    ↓
Immutable AnalysisSnapshot
    ↓
Multiple isolated Agent runtimes
    ↓
RecordOnly / optional soft evidence
    ↓
RiskManager → PortfolioManager → ExecutionManager
```

These systems must complement, not replace:

```text
DBSCAN support/resistance
Swings and market structure
Channels/trendlines
Price action
Regime analysis
NEoWave
Setup calibration
Meta-model
TradeManager
```

---

# 2. Non-negotiable rules

```text
Only completed candles create strategic evidence.
No object may be visible before its confirmation/availability time.
Historical origin time and legal availability time must be separate.
Published snapshots are deeply immutable.
Compatible Agents share snapshots.
Different analysis profiles get separate snapshots.
Agents never mutate analysis state.
RecordOnly is the default.
Missing or uncertain evidence is neutral.
Neither subsystem places orders or calculates final quantity.
Risk influence is always within 0..1 and can never raise base risk.
Open trades retain entry-time zone/pool identities.
Newly redrawn structures cannot silently rewrite an open trade.
No hard entry rejection without purged walk-forward evidence.
Simulator and live use the same implementation.
```

Correct terminology:

```text
Price-derived supply/demand zone
Price-inferred liquidity pool
Inferred buy-side/sell-side liquidity
```

Do not claim known institutional orders or global FX order-book visibility.

---

# 3. Shared snapshot contracts

Extend the existing immutable analysis snapshot:

```csharp
public sealed record SupplyDemandAnalysisSnapshot
{
    public required bool IsEnabled { get; init; }
    public required string ProfileHash { get; init; }
    public required long SnapshotVersion { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required IReadOnlyList<SupplyDemandZone> ActiveZones { get; init; }
    public required IReadOnlyList<SupplyDemandZoneEvent> RecentEvents { get; init; }
    public required SupplyDemandAnalysisQuality Quality { get; init; }
}

public sealed record LiquidityAnalysisSnapshot
{
    public required bool IsEnabled { get; init; }
    public required string ProfileHash { get; init; }
    public required long SnapshotVersion { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required IReadOnlyList<LiquidityPool> ActivePools { get; init; }
    public required IReadOnlyList<LiquidityEvent> RecentEvents { get; init; }
    public required LiquidityAnalysisQuality Quality { get; init; }
}
```

Every object must retain:

```text
Instrument
Timeframe
Profile hash
Snapshot version
Origin time
ConfirmedAt
AvailableAt
```

Cache by:

```text
Instrument × ProfileHash × Timeframe × DecisionEpoch × SnapshotVersion
```

---

# 4. Analysis profile separation

## Supply/demand calculation profile

Include every setting that changes calculation:

```text
ATR period
Base candle min/max
Maximum base range ATR
Maximum average base body ATR
Minimum overlap
Maximum base efficiency
Minimum departure ATR
Minimum departure efficiency
Minimum directional body ratio
Maximum departure candles
Structure-break requirement
Fair-value-gap settings
Boundary mode
Invalidation mode
Expiry
Merge/nesting settings
Scoring weights
Rule-set version
```

## Liquidity calculation profile

Include:

```text
Confirmed swing configuration
Equal-level ATR tolerance
Source point min/max
Maximum pool width ATR
Minimum swing prominence
Range rules
Session/day/week reference rules
Round-number rules
Merge rules
Sweep penetration threshold
Close-back threshold
Accepted-break rules
Displacement confirmation
Structure-shift confirmation
Expiry
Scoring weights
Rule-set version
```

Agent interpretation thresholds belong to Agent policy, not the analysis profile.

---

# 5. Supply and demand subsystem

## 5.1 Patterns

Implement:

```text
Drop-Base-Rally
Rally-Base-Rally
Rally-Base-Drop
Drop-Base-Drop
```

```csharp
public enum SupplyDemandPattern
{
    DropBaseRally,
    RallyBaseRally,
    RallyBaseDrop,
    DropBaseDrop
}

public enum SupplyDemandZoneType
{
    Demand,
    Supply
}
```

## 5.2 Zone model

```csharp
public sealed record SupplyDemandZone
{
    public required Guid ZoneId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required BarInterval Interval { get; init; }
    public required SupplyDemandZoneType Type { get; init; }
    public required SupplyDemandPattern Pattern { get; init; }

    public required decimal ProximalPrice { get; init; }
    public required decimal DistalPrice { get; init; }

    public required DateTimeOffset BaseStartedAt { get; init; }
    public required DateTimeOffset BaseEndedAt { get; init; }
    public required DateTimeOffset DepartureStartedAt { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }

    public required SupplyDemandZoneState State { get; init; }
    public required int BaseCandleCount { get; init; }
    public required int TouchCount { get; init; }

    public required decimal DepartureAtr { get; init; }
    public required decimal DepartureEfficiency { get; init; }
    public required decimal BaseCompactness { get; init; }
    public required decimal ImbalanceRatio { get; init; }
    public required decimal PenetrationRatio { get; init; }
    public required decimal FreshnessScore { get; init; }
    public required decimal QualityScore { get; init; }

    public required bool BrokeStructure { get; init; }
    public required bool HasFairValueGap { get; init; }

    public required long SnapshotVersion { get; init; }
    public required string ProfileHash { get; init; }
}
```

## 5.3 Lifecycle

```csharp
public enum SupplyDemandZoneState
{
    Forming,
    ConfirmedFresh,
    Approached,
    Tested,
    PartiallyMitigated,
    Mitigated,
    Invalidated,
    Expired,
    Merged
}
```

Preserve transitions as append-only events. Never silently rewrite or delete history.

## 5.4 Objective base detection

Use configured measurements:

```text
Base candle count
Total base range / ATR
Average body / ATR
Candle overlap
Directional efficiency
Contained closes
```

Initial research defaults only:

```text
MinimumBaseCandles = 1
MaximumBaseCandles = 6
MaximumBaseRangeAtr = 1.25
MaximumAverageBodyAtr = 0.35
MinimumCandleOverlapRatio = 0.40
MaximumBaseEfficiencyRatio = 0.35
```

## 5.5 Objective departure detection

Require:

```text
Minimum total departure in ATR
Minimum directional efficiency
Minimum directional body ratio
Maximum departure duration
Limited overlap
Optional structure break
Optional fair-value gap
```

Initial research defaults:

```text
MinimumDepartureAtr = 1.50
MinimumDepartureEfficiency = 0.65
MinimumDirectionalBodyRatio = 0.55
MaximumDepartureCandles = 5
RequireStructureBreak = false
```

A zone becomes visible only after departure confirmation.

## 5.6 Boundary modes

```csharp
public enum ZoneBoundaryMode
{
    FullWickRange,
    BodyToExtreme,
    DepartureOriginBody
}
```

Demand:

```text
Proximal = upper edge nearest current price
Distal = lower origin extreme
```

Supply:

```text
Proximal = lower edge nearest current price
Distal = upper origin extreme
```

The boundary mode is immutable for the zone.

## 5.7 Invalidation

```csharp
public enum ZoneInvalidationMode
{
    CloseBeyondDistal,
    WickBeyondDistal,
    PenetrationThreshold
}
```

Default:

```text
CloseBeyondDistal
```

Demand invalidates after a completed close below distal.  
Supply invalidates after a completed close above distal.

## 5.8 Freshness and mitigation

Track:

```text
Touch count
First revisit
Maximum wick penetration
Maximum close penetration
Time spent inside
Reaction MFE/MAE
Full traversal
```

Freshness should decline through explicit score components, not hidden state.

## 5.9 Scoring

Record components separately:

```text
Departure strength
Departure efficiency
Base compactness
Base duration
Structure break
Fair-value gap
Freshness
Touch penalty
Penetration penalty
Age penalty
Higher-timeframe alignment
Support/resistance confluence
Liquidity confluence
Room to opposing zone
```

`QualityScore` is not a probability.

## 5.10 Merge and nesting

Specify deterministic rules for:

```text
Overlapping same-side zones
Nested zones
Same departure producing duplicates
Cross-timeframe zones
Opposing overlapping zones
```

Never merge opposing supply and demand. Preserve source IDs and merge lineage.

---

# 6. Price-inferred liquidity subsystem

## 6.1 Sides and types

```csharp
public enum LiquiditySide
{
    BuySide,
    SellSide
}

public enum LiquidityPoolType
{
    EqualHighs,
    EqualLows,
    SwingHigh,
    SwingLow,
    RangeHigh,
    RangeLow,
    PreviousSessionHigh,
    PreviousSessionLow,
    PreviousDayHigh,
    PreviousDayLow,
    PreviousWeekHigh,
    PreviousWeekLow,
    RoundNumber
}
```

This phase must use price inference only. Do not add broker order-book data.

## 6.2 Pool model

```csharp
public sealed record LiquidityPool
{
    public required Guid PoolId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required BarInterval Interval { get; init; }

    public required LiquiditySide Side { get; init; }
    public required LiquidityPoolType Type { get; init; }

    public required decimal LowerPrice { get; init; }
    public required decimal UpperPrice { get; init; }
    public required decimal ReferencePrice { get; init; }

    public required DateTimeOffset OriginatedAt { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }

    public required LiquidityPoolState State { get; init; }
    public required int SourcePointCount { get; init; }
    public required int TouchCount { get; init; }

    public required decimal EqualnessScore { get; init; }
    public required decimal VisibilityScore { get; init; }
    public required decimal CompressionScore { get; init; }
    public required decimal ProminenceScore { get; init; }
    public required decimal FreshnessScore { get; init; }
    public required decimal QualityScore { get; init; }

    public required long SnapshotVersion { get; init; }
    public required string ProfileHash { get; init; }
}
```

## 6.3 Lifecycle

```csharp
public enum LiquidityPoolState
{
    Forming,
    Active,
    Approached,
    Touched,
    Swept,
    Consumed,
    AcceptedBreak,
    Broken,
    Expired,
    Merged
}
```

A sweep and an accepted breakout are different terminal interpretations.

## 6.4 Equal highs/lows

Reuse existing confirmed swings.

Do not require exact equality:

```text
absolute level difference ≤ ATR × EqualLevelTolerance
```

Initial research defaults:

```text
EqualLevelToleranceAtr = 0.10
MinimumSourcePoints = 2
MaximumSourcePoints = 6
MaximumPoolWidthAtr = 0.20
```

Consider:

```text
Swing prominence
Time separation
Source point count
Pool width
Reaction quality
Structural degree
Prior consumption
```

## 6.5 Isolated swing liquidity

A confirmed prominent swing may create lower-confidence liquidity even without equal levels.

Require:

```text
Confirmed swing
AvailableAt reached
Minimum prominence
Minimum age
Not already consumed
```

## 6.6 Range liquidity

Range-high/range-low pools require a causal confirmed range:

```text
Minimum duration
Minimum touches
Bounded width
No accepted breakout
```

Reuse existing range/structure analysis where available.

## 6.7 Session/day/week levels

Create only after the reference period completes:

```text
Previous session high/low
Previous day high/low
Previous week high/low
```

Use one authoritative session/timezone service. Persist reference period start/end and `AvailableAt`.

## 6.8 Round numbers

Make round-number steps instrument-aware through:

```text
Pip location
Price precision
Asset class
Configured major/minor steps
```

Treat round-number-only pools as lower-confidence unless confluence exists.

---

# 7. Liquidity events

## 7.1 Event types

```csharp
public enum LiquidityEventType
{
    Approach,
    Touch,
    UnconfirmedPenetration,
    Sweep,
    AcceptedBreak,
    Retest,
    Consumption,
    Failure
}
```

## 7.2 Sweep model

```csharp
public sealed record LiquiditySweepEvent
{
    public required Guid SweepId { get; init; }
    public required Guid PoolId { get; init; }

    public required DateTimeOffset SweepStartedAt { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }

    public required decimal ExtremePrice { get; init; }
    public required decimal PenetrationAtr { get; init; }

    public required bool ClosedBackInside { get; init; }
    public required bool ClosedBackBeyondOriginSide { get; init; }
    public required bool DisplacementConfirmed { get; init; }
    public required bool StructureShiftConfirmed { get; init; }

    public required decimal RejectionStrength { get; init; }
    public required decimal QualityScore { get; init; }
    public required long SnapshotVersion { get; init; }
}
```

## 7.3 Buy-side sweep

```text
Pool existed and was available
→ completed price penetrates above upper boundary
→ minimum penetration reached
→ completed close returns inside/below pool
→ optional bearish displacement
→ optional bearish structure shift
```

## 7.4 Sell-side sweep

```text
Pool existed and was available
→ completed price penetrates below lower boundary
→ minimum penetration reached
→ completed close returns inside/above pool
→ optional bullish displacement
→ optional bullish structure shift
```

## 7.5 Accepted breakout

Require:

```text
Completed close beyond pool
Minimum close distance
Follow-through or hold period
No immediate close-back
Optional retest
Optional displacement
```

Return one of:

```text
Sweep
AcceptedBreak
UnconfirmedPenetration
```

Never reduce all three to “touched”.

---

# 8. Confluence engine

Implement explicit relationships, not hidden score mixing.

Bullish sequence:

```text
Sell-side liquidity
→ sweep
→ demand-zone interaction
→ bullish displacement
→ bullish structure shift
→ price-action confirmation
```

Bearish sequence:

```text
Buy-side liquidity
→ sweep
→ supply-zone interaction
→ bearish displacement
→ bearish structure shift
→ price-action confirmation
```

```csharp
public sealed record SupplyDemandLiquidityConfluence
{
    public required Guid ConfluenceId { get; init; }
    public required Guid ZoneId { get; init; }
    public required Guid? PoolId { get; init; }
    public required Guid? SweepId { get; init; }

    public required ConfluenceDirection Direction { get; init; }
    public required bool ZoneWasFresh { get; init; }
    public required bool LiquidityWasSwept { get; init; }
    public required bool DisplacementConfirmed { get; init; }
    public required bool StructureShiftConfirmed { get; init; }

    public required decimal DistanceBetweenZoneAndPoolAtr { get; init; }
    public required decimal QualityScore { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
}
```

Do not call the score a win probability.

---

# 9. Multi-timeframe use

Recommended roles:

```text
H4/H1:
    external liquidity
    major zones
    broad targets/invalidation

M15:
    setup zones
    equal highs/lows
    ranges

M5:
    sweep
    displacement
    structure shift
    entry location

M1:
    optional execution refinement only
```

Reject mixed snapshot versions or stale higher-timeframe evidence.

---

# 10. Agent integration

Use:

```csharp
public enum StructuralEvidenceMode
{
    Disabled,
    RecordOnly,
    SoftConfidence,
    SoftRiskReduction,
    SoftConfidenceAndRisk
}
```

Keep separate settings:

```text
SupplyDemandEvidenceMode
LiquidityEvidenceMode
```

Default:

```text
Enabled = true
EvidenceMode = RecordOnly
```

## Supply/demand features

```text
Nearest demand/supply
Zone type/state/quality
Age/freshness/touches
Penetration
Distance to proximal/distal in ATR
Inside/near/opposed relationship
Higher-timeframe alignment
Room to opposing zone
R:R to opposing zone
Structure-break/FVG evidence
```

## Liquidity features

```text
Nearest buy-side/sell-side pool
Pool type/state/quality
Age/source count/freshness
Distance in ATR
Approach/touch/sweep/break state
Penetration
Close-back
Displacement
Structure shift
External/internal classification
Zone confluence
```

Rules:

```text
Aligned evidence may provide bounded soft support.
Conflicting evidence may reduce confidence/risk.
Missing/uncertain evidence is neutral.
Risk multiplier remains in 0..1.
```

---

# 11. Stops, targets, and management

## Structural stops

Long:

```text
below demand distal boundary
+ spread/slippage/ATR buffer
```

Short:

```text
above supply distal boundary
+ buffer
```

Avoid placing stops exactly at obvious unswept liquidity.

RiskManager validates geometry and calculates quantity.

## Targets

Long:

```text
before valid buy-side liquidity
or before significant supply proximal boundary
```

Short:

```text
before valid sell-side liquidity
or before significant demand proximal boundary
```

Include cost, intervening structures, target-front-running buffer, and expected R.

## Entry-pinned management references

At position open persist:

```text
Entry zone ID and boundaries
Zone state/profile hash
Target liquidity pool ID
Invalidation reference
Liquidity profile hash
Management policy revision
```

Potential later management actions, disabled by default:

```text
Scale out before opposing pool/zone
Protect profit after targeted sweep
Trail behind new confirmed zones
Avoid stop movement into active liquidity
Exit after entry-zone invalidation
Exit after accepted breakout against position
```

No later redraw may silently replace the original thesis.

---

# 12. Feature switches

Add:

```text
SupplyDemandEnabled
SupplyDemandEvidenceMode
SupplyDemandStructuralStopsEnabled
SupplyDemandTargetsEnabled
SupplyDemandManagementEnabled

LiquidityEnabled
LiquidityEvidenceMode
LiquidityTargetsEnabled
LiquidityStopAvoidanceEnabled
LiquidityManagementEnabled

SupplyDemandLiquidityConfluenceEnabled
```

Trace each through:

```text
Configuration
→ serialization
→ API/CLI
→ mapper
→ runtime policy
→ analysis/Agent/TradeManager
→ diagnostics
```

False must fully bypass influence.

---

# 13. PostgreSQL persistence

Suggested tables:

```text
analytics.supply_demand_zones
analytics.supply_demand_zone_events
analytics.zone_candidate_relationships
analytics.zone_trade_relationships
analytics.zone_outcomes

analytics.liquidity_pools
analytics.liquidity_pool_events
analytics.liquidity_sweeps
analytics.liquidity_candidate_relationships
analytics.liquidity_trade_relationships
analytics.liquidity_outcomes
```

Persist:

```text
Stable ID
Instrument/timeframe/profile
Origin/confirmation/availability
Boundaries
State/lifecycle
Score components
Source swing IDs
Touch/penetration data
Sweep/break data
Confluence IDs
Snapshot version
Candidate/trade lineage
```

Use bounded `Channel<T>`, async batch writers, Npgsql batching/COPY where appropriate, and no shared `DbContext`.

No synchronous database call inside Agent evaluation.

---

# 14. Attribution and research

Measure supply/demand outcomes:

```text
First-touch reaction
Repeated-touch reaction
MFE/MAE
Time to reaction
Time to invalidation
Target before invalidation
Fresh versus tested
Structure-break contribution
FVG contribution
Timeframe/instrument/session/regime
```

Measure liquidity outcomes:

```text
Pool reached
Sweep/accepted break
MFE/MAE after event
Time to opposing pool
First versus repeated sweep
Pool-type performance
Round-number contribution
Zone confluence contribution
```

Experiments:

```text
Baseline
Supply/demand RecordOnly
Liquidity RecordOnly
Both RecordOnly
Soft ranking
Soft risk reduction
Structural stops/targets
Validated management
```

Use:

```text
WalkForwardPlanner
PurgedTimeSeriesCrossValidator
Embargo
Untouched final test
```

Never calibrate on the full evaluation window.

---

# 15. Dashboard

Supply/demand overlays:

```text
Fresh zone: strong shaded rectangle
Tested: reduced visibility
Mitigated: dashed/faded
Invalidated: terminate at invalidation candle
```

Liquidity overlays:

```text
Buy-side/sell-side horizontal bands
Approach highlight
Sweep marker
Accepted-break marker
Consumed/expired fade
```

Tooltips must show IDs, timeframe, profile, boundaries, state, origin, `ConfirmedAt`, `AvailableAt`, quality components, touches, penetration, sweep/break status, and confluence.

Shared analysis must not be duplicated per Agent. Agent overlays show the evidence each Agent used.

---

# 16. Concurrency and performance

```text
One chronological update stream per instrument/profile
Different instruments/profiles may run concurrently
One profile runtime processes sequentially
Published snapshots are immutable
Identical profiles compute once
```

Use:

```text
Task
async/await
Channel<T>
Task.WhenAll where safe
bounded SemaphoreSlim
```

Prohibit:

```text
Unbounded Task.Run
One thread per Agent
Shared mutable dictionaries
Shared DbContext
.Result / .Wait()
```

Bound:

```text
Active zones per timeframe
Active pools per timeframe
Retained events
Historical lookback
Merged source count
Chart overlays
```

Pruning must be deterministic and unrelated to future profitability.

---

# 17. Deterministic IDs

Derive stable IDs from normalized inputs:

```text
Instrument
Timeframe
Profile hash
Origin timestamps
Source swing IDs
Boundaries
Pattern/type
Rule-set version
```

Replay must produce identical zone, pool, sweep, and confluence IDs.

---

# 18. Required tests

## Supply/demand

```text
All four patterns
Deterministic IDs
No early visibility
Long/short symmetry
Base thresholds
Departure thresholds
Boundary modes
Structure-break/FVG options
Fresh/tested/mitigated/invalidated/expired
Merge/nesting
No opposing merge
No duplicates
Replay/reconnect parity
Profile separation
Immutable sharing
```

## Liquidity

```text
Equal highs/lows
Swing pools
Range pools
Previous session/day/week
Round numbers
ATR tolerance
No early visibility
Approach/touch/penetration
Sweep
Accepted breakout
Consumption/expiry/merge
Long/short symmetry
Replay parity
Immutable sharing
```

## Confluence

```text
Sell-side sweep into demand
Buy-side sweep into supply
Zone-only
Sweep-only
Sweep + zone + displacement
Sweep + zone + structure shift
No future evidence
Deterministic scoring
```

## Agent/risk

```text
Disabled fully bypasses
RecordOnly changes diagnostics only
Soft confidence bounded
Risk multiplier in 0..1
Missing/uncertain neutral
Parallel/sequential decisions identical
Multiple Agents share compatible snapshots
Different profiles remain separate
```

## TradeManager

```text
Entry-pinned references survive redraw
Management disabled means no action
Duplicate action prevented
Emergency safety precedence
```

## Leakage

Replay candle-by-candle and prove:

```text
No zone before confirmed departure
No swing pool before swing confirmation
No equal-level pool using future pivots
No period high/low before period completion
No sweep before confirming close
No structure shift before confirmation
No later chart annotation visible in earlier snapshot
```

---

# 19. Implementation phases

```text
1. Audit existing extension points and parity paths
2. Domain contracts, configuration, hashes, stable IDs
3. Supply/demand detector and lifecycle
4. Liquidity pool detector and lifecycle
5. Sweep versus accepted-break engine
6. Confluence engine
7. Shared snapshot/profile cache integration
8. Agent RecordOnly integration
9. Dashboard/chart rendering
10. PostgreSQL persistence and outcome attribution
11. Optional soft influence after validation
12. Management rules after held-out evidence
```

Audit first. Do not begin by rewriting existing swings or ChartAnnotator.

---

# 20. Acceptance criteria

Complete only when:

```text
Both systems are causal and deterministic.
Origin, confirmation, and availability are distinct.
No future candle can influence earlier output.
Existing confirmed swings are reused.
Snapshots are immutable and shared safely.
Different profiles remain isolated.
RecordOnly is default.
Uncertainty is neutral.
Risk cannot increase.
No subsystem places trades.
RiskManager and PortfolioManager remain authoritative.
Open positions retain entry-time lineage.
Simulator and live use the same path.
Chart lifecycle is visible.
Persistence is async where safe.
Leakage, replay, symmetry, concurrency, and parity tests pass.
```

---

# 21. Deliverables

Return:

1. updated source ZIP;
2. implementation report;
3. architecture report;
4. test report;
5. performance report;
6. issue register;
7. SHA-256 checksum.

Include Mermaid diagrams for:

```text
Shared analysis pipeline
Supply/demand lifecycle
Liquidity lifecycle
Sweep versus accepted breakout
Confluence flow
Multi-timeframe flow
Agent evidence path
Stop/target path
Entry-pinned management lineage
PostgreSQL persistence flow
```

End the report with direct answers:

1. Are zones and pools causal?
2. Can anything appear before `AvailableAt`?
3. Are existing swings reused?
4. How are equal levels defined?
5. How is sweep distinguished from breakout?
6. How are zones invalidated and pools consumed?
7. How are overlaps merged?
8. Do these complement support/resistance, price action, and NEoWave?
9. Is uncertainty neutral?
10. Can risk increase?
11. Can either subsystem place trades?
12. Are open trades entry-pinned?
13. Can multiple Agents share snapshots?
14. Are simulator and live equivalent?
15. Is it safe for OANDA Practice shadow testing?

Be strict and conservative.

Do not market inferred liquidity as actual order-book liquidity.

Do not weaken existing rules merely to increase trade count.

Do not enable hard filters without purged walk-forward proof.

Do not allow concurrency to change deterministic outputs.

Do not silently repaint historical analysis.
