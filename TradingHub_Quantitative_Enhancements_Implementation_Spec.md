# TradingHub Quantitative Enhancements — Production Implementation Specification

**Target codebase:** `TradingHub_Final_Phase2_QuantRisk_MTF`  
**Audience:** AI coding agent or senior quantitative developer  
**Primary language/runtime:** C# / .NET 10, Vue/TypeScript Dashboard  
**Purpose:** Implement the remaining quantitative setup-selection, portfolio-risk, adaptive-sizing, trade-management, execution-modelling, and validation systems without weakening the existing deterministic simulator.

---

## 1. Mission

Extend TradingHub from an independent, single-instrument strategy simulator into a quantitatively controlled trading platform that can:

1. classify the current market regime;
2. route each strategy to rules suitable for that regime;
3. evaluate additional independent market evidence without stacking redundant indicators;
4. allocate risk across multiple instruments and strategies;
5. reserve margin and capital for future opportunities;
6. control currency and correlation concentration;
7. protect account and strategy equity high-watermarks;
8. reduce position risk during drawdown, volatility stress, poor liquidity, or degraded execution;
9. calibrate setup confidence and trade-management rules from out-of-sample evidence;
10. simulate realistic spread, slippage, financing, gaps, partial fills, and order failures;
11. support both independent strategy comparison and a shared real-account portfolio mode;
12. produce sufficient diagnostics to explain every accepted, resized, delayed, rejected, reduced, or closed trade.

This specification is intentionally explicit. Do not implement isolated helper classes that are not connected to the real path:

```text
Market data
    → ChartAnnotator
    → Agent
    → PositionSizer
    → PortfolioRiskManager / CapitalAllocator
    → PreTradeRiskManager
    → ExecutionManager
    → Broker or SimulatedBroker
    → TradeManager
    → Replay / Performance / Dashboard
```

---

## 2. Existing baseline that must be preserved

The current codebase already contains:

- streamed historical simulation;
- execution and analysis-base candle separation;
- completed-candle-only multi-timeframe aggregation;
- shared immutable analysis snapshots;
- independent per-strategy mutable state;
- deterministic per-frame worker barrier;
- Legacy and Improved progressive agents;
- role-based multi-timeframe trend/setup/confirmation/entry evidence;
- ATR, RSI, Bollinger Bands, ADX/DMI;
- swings, support/resistance zones, RANSAC trendlines, channels;
- BOS, ChoCH, retest, rejection, displacement, compression/expansion and liquidity-sweep price action;
- fixed, cash-risk and fractional-risk position sizing;
- pre-trade stop, reward/risk, margin and pyramiding checks;
- execution-frame mechanical position protection;
- fast, main and thesis trade-management intervals;
- scaling out, break-even, profit floors, MFE giveback and structural trailing;
- daily/weekly safety controls;
- replay events, trade records, performance files and Dashboard playback;
- broker and asset catalog selection.

Do not remove or bypass these systems.

---

## 3. Non-negotiable correctness invariants

Every implementation phase must preserve all of the following.

### 3.1 No lookahead

- Use only completed candles.
- A snapshot may become available only at its candle close.
- A decision produced from frame `N` must not fill before frame `N+1`.
- Warm-up may initialise and calibrate features, but evaluation-period outcomes may not alter frozen calibration.
- Economic events may be used only when their publication schedule was known at the simulated time.
- Correlations, percentiles, confidence calibration and regime labels must use trailing data only.

### 3.2 Determinism

Given identical:

- input candles;
- broker model;
- configuration;
- seed;
- source schema version;

the simulator must produce identical:

- decisions;
- quantities;
- fills;
- events;
- trades;
- balances;
- performance;
- output hashes.

Never use unordered dictionary enumeration as a trading priority. Always sort explicitly.

### 3.3 Strategy isolation in independent mode

Legacy and Improved must retain separate:

- broker state;
- balance/equity;
- positions;
- pending orders;
- safety controller;
- trade manager state;
- journal;
- replay events.

### 3.4 Shared-account atomicity in portfolio mode

In shared mode:

- all strategies use one authoritative account and margin pool;
- risk reservation and order admission must be atomic;
- two strategies must not reserve the same available risk or margin;
- order priority must be deterministic;
- pending-order risk must be included;
- fills and cancellations must update reservations exactly once.

### 3.5 Protective-order safety

- Stops never widen risk after entry unless an explicitly supported emergency recovery policy says otherwise. The default is never.
- Stop quantity and target quantity must equal remaining position quantity after partial fills.
- Close orders must be reduce-only.
- Oversized stale closes must be clamped to the remaining quantity.
- Protective orders must never reverse a position.
- Full exit overrides partial reduction and stop amendment.
- A partial-exit stage executes at most once.

### 3.6 Honest data semantics

- Do not call broker tick volume “centralised Forex volume”.
- Do not calculate order-flow delta without reliable aggressor-side trades.
- Do not fabricate historical bid/ask data from midpoint and label it historical bid/ask.
- Synthetic spread/slippage must be clearly identified in manifests.
- Missing conversion, financing, event, or liquidity data must produce explicit degraded-mode diagnostics or rejection, not silent assumptions.

### 3.7 Backward compatibility

- Existing requests and saved configurations should continue to load where practical.
- New fields require defaults.
- Persisted schemas require explicit versions.
- Old files with absent collections must deserialize as empty collections.
- Corrupt records must be skipped or quarantined without poisoning the whole simulation service.

---

## 4. Required project boundaries

Add the following standalone projects.

```text
PortfolioManager/
    PortfolioManager.csproj

QuantResearch/
    QuantResearch.csproj
```

### 4.1 `PortfolioManager`

Owns:

- portfolio heat;
- risk reservations;
- margin reservations;
- strategy budgets;
- instrument limits;
- currency exposure;
- correlation clusters;
- opportunity ranking;
- shared-account capital allocation.

References should remain minimal:

```text
PortfolioManager
    → Brokers
    → Agent (only if AgentDecision remains the intent model)
```

Prefer moving generic intent/risk contracts into `TradingCore` if this prevents a circular dependency.

`RiskManager` remains responsible for:

- per-order sizing;
- per-order pre-trade validation;
- account/strategy safety state;
- drawdown and equity protection.

`PortfolioManager` is responsible for combined portfolio decisions.

### 4.2 `QuantResearch`

Owns offline evaluation only:

- walk-forward orchestration;
- purged time-series folds;
- Monte Carlo;
- confidence calibration;
- feature ablation;
- parameter sensitivity;
- MAE/MFE cohort analysis;
- regime performance analysis.

It must not be referenced by Agent, RiskManager, TradeManager, Simulator core, or live hosts.

The live/simulator runtime must consume exported immutable calibration artefacts, not run research optimisation while trading.

---

# PART A — MARKET FEATURES, REGIME CLASSIFICATION AND SETUP QUALITY

## 5. Add incremental Efficiency Ratio

### 5.1 Purpose

Measure whether recent movement is directional or noisy.

For period `N`:

```text
directionalMovement = abs(close[t] - close[t-N])
pathMovement = sum(abs(close[i] - close[i-1])) for the N transitions
efficiencyRatio = pathMovement == 0 ? 0 : directionalMovement / pathMovement
```

Expected range: `0..1`.

### 5.2 Files

Create:

```text
ChartAnnotator/Indicators/EfficiencyRatioState.cs
```

Update:

```text
ChartAnnotator/Models/AnalysisModels.cs
ChartAnnotator/Engine/ChartAnnotationOptions.cs
ChartAnnotator/Engine/ChartAnnotationEngine.cs
DashboardContracts/ReplayContracts.cs
```

### 5.3 API

```csharp
public sealed class EfficiencyRatioState
{
    public EfficiencyRatioState(int period);
    public bool IsReady { get; }
    public decimal Current { get; }
    public decimal Update(decimal close);
}
```

Use bounded incremental state. Do not recalculate the entire candle history every update.

Add to `IndicatorSnapshot`:

```csharp
public decimal? EfficiencyRatio { get; init; }
public EfficiencyAnalysisSnapshot EfficiencyAnalysis { get; init; }
    = EfficiencyAnalysisSnapshot.Empty;
```

Suggested analysis:

```csharp
public enum MarketEfficiencyState
{
    Unknown,
    HighlyChoppy,
    Choppy,
    Transitional,
    Efficient,
    HighlyEfficient
}
```

Percentile thresholds should be warm-up calibrated per instrument/timeframe where sufficient history exists. Provide deterministic fallback absolute thresholds.

### 5.4 Tests

- flat prices return `0`;
- monotonic movement returns approximately `1`;
- alternating movement produces lower ER;
- bounded state equals a trusted batch implementation;
- no result before warm-up completion;
- exact deterministic decimal behaviour.

---

## 6. Add Donchian breakout context

### 6.1 Purpose

Provide an objective rolling breakout boundary independent from RANSAC and confirmed swing structures.

### 6.2 Files

Create:

```text
ChartAnnotator/Indicators/DonchianState.cs
```

Update `IndicatorSnapshot`.

### 6.3 Model

```csharp
public sealed record DonchianSnapshot
{
    public decimal? Upper { get; init; }
    public decimal? Lower { get; init; }
    public decimal? Middle { get; init; }
    public decimal? Width { get; init; }
    public decimal? WidthAtr { get; init; }
    public bool ClosedAbovePreviousUpper { get; init; }
    public bool ClosedBelowPreviousLower { get; init; }
    public int BarsSinceUpperBreak { get; init; }
    public int BarsSinceLowerBreak { get; init; }
}
```

Important: compare the latest close against the **previous completed channel boundary**, not a boundary that already includes the latest candle. Otherwise the breakout test becomes self-referential.

### 6.4 Strategy usage

Donchian is context, not an unconditional entry.

A breakout setup may require:

```text
primary trend aligned
AND previous Donchian boundary broken by completed close
AND displacement or expansion present
AND spread/ATR acceptable
AND optional retest confirmed
```

Do not add Donchian directly to confidence as a permanent positive contribution. Score according to direction and setup type.

### 6.5 Tests

- current candle cannot create and break its own boundary;
- high/low windows evict correctly;
- breakout direction is correct;
- historical sequence is deterministic.

---

## 7. Add anchored value reference: VWAP when valid, TWAP fallback

### 7.1 Purpose

Measure location relative to accepted value and whether a pullback returns to a favourable reference.

### 7.2 Data semantics

Create:

```csharp
public enum ValueReferenceKind
{
    Unavailable,
    AnchoredTwap,
    BrokerTickVolumeVwap,
    ExchangeVolumeVwap
}
```

Do not label tick-volume VWAP as exchange-volume VWAP.

### 7.3 Anchors

Support:

```csharp
public enum ValueAnchorType
{
    SessionOpen,
    WeekOpen,
    MajorSwing,
    StructureBreak,
    SetupStart
}
```

Initial production scope:

- session open;
- week open;
- latest confirmed major swing;
- latest confirmed BOS/ChoCH.

Do not implement arbitrary event anchors until event data is available.

### 7.4 Model

```csharp
public sealed record AnchoredValueReference
{
    public required string AnchorId { get; init; }
    public required ValueAnchorType AnchorType { get; init; }
    public required DateTimeOffset AnchoredAt { get; init; }
    public required ValueReferenceKind Kind { get; init; }
    public required decimal Value { get; init; }
    public decimal? StandardDeviation { get; init; }
    public decimal? DistanceAtr { get; init; }
    public decimal? DataCoveragePercent { get; init; }
}
```

### 7.5 Integration

Add a bounded list to `AnalysisSnapshot`:

```csharp
public IReadOnlyList<AnchoredValueReference> ValueReferences { get; init; } = [];
```

The Agent may use:

- bullish pullback near an anchored value;
- rejection of value in trend direction;
- excessive stretch as a reason not to chase;
- loss of value plus adverse structure as management evidence.

### 7.6 Tests

- anchor starts from the correct candle;
- volume-less data produces TWAP;
- VWAP numerator/denominator are incremental and stable;
- reset on session/week boundaries is correct;
- no use of future swing confirmation before `ConfirmedAt`.

---

## 8. Implement authoritative market-regime classification

### 8.1 New files

```text
ChartAnnotator/Regime/MarketRegimeClassifier.cs
ChartAnnotator/Regime/MarketRegimeModels.cs
ChartAnnotator/Regime/MarketRegimeOptions.cs
ChartAnnotator/Regime/MarketRegimeCalibration.cs
```

### 8.2 Regimes

```csharp
public enum MarketRegime
{
    Unknown,
    TrendingUp,
    TrendingDown,
    Range,
    Compression,
    BreakoutExpansionUp,
    BreakoutExpansionDown,
    HighVolatilityDisorder,
    IlliquidUnsafe
}
```

A regime snapshot must include more than a label:

```csharp
public sealed record MarketRegimeSnapshot
{
    public required MarketRegime Regime { get; init; }
    public required decimal Confidence { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
    public required int AgeCandles { get; init; }
    public required IReadOnlyList<RegimeContribution> Contributions { get; init; }
    public required string ReasonCode { get; init; }
    public bool IsTradeable { get; init; }
}
```

### 8.3 Inputs

Use existing and newly added independent features:

- ADX and +DI/-DI;
- market-structure direction and persistence;
- ATR percentile and ATR direction;
- Bollinger bandwidth percentile/direction;
- Efficiency Ratio;
- compression/expansion price-action state;
- displacement frequency;
- Donchian breakout;
- spread/ATR, where spread is available;
- data quality.

Do not use trade outcomes.

### 8.4 Deterministic rule hierarchy

Recommended initial rules:

#### Illiquid/unsafe

Highest priority:

```text
critical data-quality failure
OR spreadAtr >= hardSpreadAtr
OR stale market data
```

#### High-volatility disorder

```text
ATR percentile >= extreme threshold
AND Efficiency Ratio low
AND structure mixed/unstable
```

#### Breakout expansion

```text
prior regime compression/range
AND Donchian or confirmed structure break
AND Bollinger expansion
AND displacement aligned
AND ER rising
```

#### Trend

```text
ADX above calibrated threshold
AND DI direction aligned
AND structure rising/falling
AND ER above minimum
AND no strong opposing break
```

#### Compression

```text
Bollinger bandwidth low percentile
AND ATR low percentile
AND recent range narrowing
```

#### Range

```text
ADX low
AND ER low
AND repeated support/resistance interaction
AND no persistent structure direction
```

#### Unknown

Insufficient or contradictory evidence.

### 8.5 Hysteresis

Prevent rapid regime flipping.

Options:

```csharp
public sealed record MarketRegimeOptions
{
    public int MinimumConfirmationBars { get; init; } = 2;
    public int MinimumPersistenceBars { get; init; } = 3;
    public decimal SwitchConfidenceMargin { get; init; } = 10m;
    public decimal MaximumTradeableSpreadAtr { get; init; } = 0.15m;
    public decimal HardMaximumSpreadAtr { get; init; } = 0.30m;
}
```

A new regime becomes authoritative only after confirmation or when an emergency unsafe condition occurs.

### 8.6 Warm-up calibration

During warm-up, collect:

- ATR distribution;
- bandwidth distribution;
- ADX distribution;
- ER distribution;
- spread/ATR distribution.

Freeze calibration at evaluation start.

Persist calibration metadata and hashes in the manifest.

### 8.7 Integration

Add:

```csharp
public MarketRegimeSnapshot MarketRegime { get; init; }
    = MarketRegimeSnapshot.Unknown;
```

to `AnalysisSnapshot`.

Update replay and live chart.

Add regime-change replay events:

```csharp
MarketRegimeChanged
MarketRegimeConfirmed
MarketRegimeUnsafe
```

---

## 9. Add regime-based strategy routing

### 9.1 Do not create separate duplicated strategy classes

Keep Legacy and Improved, but give each a regime policy.

```csharp
public sealed record RegimeStrategyPolicy
{
    public required MarketRegime Regime { get; init; }
    public bool AllowNewEntries { get; init; }
    public decimal MinimumConfidenceAdjustment { get; init; }
    public decimal RiskMultiplier { get; init; } = 1m;
    public string EntryProfileId { get; init; } = "default";
    public string ManagementProfileId { get; init; } = "default";
}
```

### 9.2 Suggested defaults

#### Trending

- permit pullback and BOS/retest setups;
- risk multiplier `1.0`;
- wider runner;
- later scale-out;
- structure trailing.

#### Range

- only permit boundary/rejection setups;
- reject midpoint entries;
- smaller target;
- risk multiplier `0.5–0.75`;
- earlier scale-out;
- no large runner by default.

#### Compression

- no entry before confirmed expansion;
- optional pending setup scope;
- risk multiplier `0` until release.

#### Breakout expansion

- require displacement plus no excessive stretch;
- avoid entry when too far from value/structure;
- moderate risk multiplier;
- use faster break-even only after sufficient MFE.

#### High-volatility disorder / unsafe

- reject entry;
- continue managing open positions;
- optionally reduce exposure according to safety policy.

### 9.3 Agent diagnostics

Every Agent decision must include:

```text
regime label
regime confidence
policy selected
risk multiplier
entry profile
management profile
reason code
```

Do not hide a regime rejection behind generic `Observe`.

---

# PART B — TRADING CONDITIONS AND EXTERNAL CONTEXT

## 10. Add trading-session and spread filter

### 10.1 New service

```text
RiskManager/Conditions/TradingConditionFilter.cs
RiskManager/Conditions/TradingConditionModels.cs
```

```csharp
public interface ITradingConditionFilter
{
    TradingConditionDecision Evaluate(TradingConditionContext context);
}
```

### 10.2 Inputs

- timestamp;
- instrument;
- current spread;
- ATR;
- market session;
- rollover window;
- holiday/session calendar;
- data freshness;
- optional scheduled event proximity;
- regime.

### 10.3 Output

```csharp
public enum TradingConditionAction
{
    Allow,
    AllowWithReducedRisk,
    DelayEntry,
    RejectEntry,
    ReduceOpenExposure
}
```

```csharp
public sealed record TradingConditionDecision
{
    public required TradingConditionAction Action { get; init; }
    public required decimal RiskMultiplier { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
}
```

### 10.4 Session model

Use IANA/UTC-safe sessions, not local-machine time.

Support:

- Asian;
- London;
- New York;
- London/New York overlap;
- broker rollover;
- weekend/pre-weekend;
- configurable holidays.

The filter must be instrument aware. A blanket “Asian session bad” rule is not acceptable.

### 10.5 Spread rules

Use spread relative to ATR:

```text
spreadAtr = executableSpread / ATR
```

Support:

- soft reduction threshold;
- hard reject threshold;
- rolling spread percentile when historical spread exists.

### 10.6 Economic calendar interface

Create only the abstraction initially:

```csharp
public interface IEconomicEventProvider
{
    IReadOnlyList<EconomicEvent> GetKnownEvents(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlySet<string> currencies);
}
```

Rules:

- only use events known at the simulated time;
- filter by currencies contained in the instrument;
- configurable blackout before/after;
- event data is for risk timing, not directional prediction;
- persist provider/version in manifest.

If no provider is configured, mark event filter `Unavailable`, not “clear”.

---

## 11. Add relative currency-strength context

### 11.1 Scope

This requires multi-instrument data. It must be optional and must not break single-instrument simulations.

### 11.2 New project placement

Models and computation may live in:

```text
PortfolioManager/CurrencyStrength/
```

or `ChartAnnotator/CrossMarket/` if it remains analysis-only. Prefer `PortfolioManager` because it also controls exposure.

### 11.3 Calculation

For each currency, use normalised returns from a configured basket.

Example:

```text
pair return = log(close[t] / close[t-lookback])
normalised return = pair return / realisedVolatility
```

Convert pair direction into base/quote contributions:

```text
GBP/USD positive return:
    positive GBP contribution
    negative USD contribution
```

Weight by:

- data quality;
- liquidity;
- inverse volatility;
- basket coverage.

Output:

```csharp
public sealed record CurrencyStrengthSnapshot
{
    public required DateTimeOffset AvailableAt { get; init; }
    public required IReadOnlyDictionary<string, decimal> Scores { get; init; }
    public required IReadOnlyDictionary<string, decimal> Coverage { get; init; }
    public required string MethodVersion { get; init; }
}
```

### 11.4 Agent use

For a long `GBP/JPY` setup:

- supportive when GBP score exceeds JPY score by threshold;
- neutral when coverage is insufficient;
- soft penalty when disagreement exists;
- hard veto only after out-of-sample validation.

Do not let currency strength duplicate the same instrument’s return excessively. Prefer leave-one-instrument-out basket computation for the traded pair.

---

# PART C — PORTFOLIO RISK, CAPITAL ALLOCATION AND SHARED ACCOUNT

## 12. Add portfolio-account mode

### 12.1 Configuration

```csharp
public enum SimulationAccountMode
{
    IndependentStrategyAccounts,
    SharedPortfolioAccount
}
```

Add to:

```text
Simulator/Models/BacktestConfiguration.cs
DashboardLive/SimulationApi.cs
BacktestRunner/BacktestCommandOptions.cs
Dashboard form and request types
Simulation manifest and configuration identity
```

Default must remain `IndependentStrategyAccounts`.

### 12.2 Independent mode

Preserve current behaviour exactly.

### 12.3 Shared mode architecture

Create one shared object per simulation:

```csharp
public sealed class SharedPortfolioRuntime
{
    public required SimulatedBrokerClient Broker { get; init; }
    public required IPortfolioRiskManager RiskManager { get; init; }
    public required ICapitalAllocator CapitalAllocator { get; init; }
    public required IPortfolioReservationBook Reservations { get; init; }
    public required ITradingSafetyController AccountSafety { get; init; }
}
```

Strategies retain independent:

- setup state;
- Agent logic;
- attribution;
- strategy risk budget;
- strategy high-watermark;
- trade-management state per owned position.

They share:

- account;
- balance;
- margin;
- position inventory;
- pending orders;
- account safety;
- portfolio limits.

### 12.4 Ownership

Every order and position must carry:

```text
StrategyId
DecisionId
SetupId
PortfolioReservationId
RiskClusterId
```

The broker position model must allow multiple strategy-owned lots in the same instrument or define deterministic netting attribution.

For OANDA-style netting, maintain an internal virtual-lot ledger per strategy even if the broker has one net position.

---

## 13. Implement reservation book

### 13.1 Why

Risk must be reserved when an order is accepted, before it fills. Otherwise simultaneous strategies can oversubscribe account capacity.

### 13.2 Interface

```csharp
public interface IPortfolioReservationBook
{
    PortfolioReservationResult TryReserve(PortfolioReservationRequest request);
    void CommitFill(string reservationId, PortfolioFillAllocation fill);
    void Release(string reservationId, PortfolioReleaseReason reason);
    PortfolioReservationSnapshot Snapshot { get; }
}
```

### 13.3 Reservation fields

```csharp
public sealed record PortfolioReservation
{
    public required string ReservationId { get; init; }
    public required string StrategyId { get; init; }
    public required string DecisionId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal PlannedStopRiskAccountCurrency { get; init; }
    public required decimal EstimatedMargin { get; init; }
    public required IReadOnlyDictionary<string, decimal> CurrencyExposureDelta { get; init; }
    public required string CorrelationClusterId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required long CreatedSequence { get; init; }
}
```

### 13.4 Atomicity

Use one lock or a single-threaded command queue inside the portfolio allocator. Do not use separate “check then reserve” calls.

---

## 14. Implement portfolio heat

### 14.1 Definition

Portfolio heat is the combined account-currency loss expected if every current position and pending order reaches its protective stop, including estimated exit costs.

```text
openHeat = sum(open position remaining stop risk)
pendingHeat = sum(reserved pending-order risk)
totalHeat = openHeat + pendingHeat
```

### 14.2 Limits

```csharp
public sealed record PortfolioRiskOptions
{
    public decimal MaximumTotalOpenRiskPercent { get; init; } = 1.5m;
    public decimal MaximumPendingRiskPercent { get; init; } = 0.75m;
    public decimal MaximumStrategyRiskPercent { get; init; } = 0.75m;
    public decimal MaximumInstrumentRiskPercent { get; init; } = 0.75m;
    public decimal MaximumCurrencyRiskPercent { get; init; } = 0.75m;
    public decimal MaximumMarginUsagePercent { get; init; } = 30m;
    public decimal MaximumSinglePositionMarginPercent { get; init; } = 10m;
    public decimal MinimumUnallocatedMarginReservePercent { get; init; } = 30m;
    public int MaximumOpenPositions { get; init; } = 3;
}
```

All percentage values are percentage points.

### 14.3 Recalculation

Recalculate after:

- entry fill;
- stop amendment;
- partial close;
- full close;
- cancellation;
- FX conversion-rate change;
- account equity change;
- financing posting.

Do not assume risk stays fixed after stop movement.

---

## 15. Implement currency-exposure manager

### 15.1 Decomposition

For units of a Forex pair:

```text
long BASE/QUOTE:
    +base currency exposure
    -quote currency exposure

short BASE/QUOTE:
    -base currency exposure
    +quote currency exposure
```

Convert exposures to account currency using contemporaneous conversion rates.

### 15.2 Interface

```csharp
public interface ICurrencyExposureCalculator
{
    CurrencyExposureResult Calculate(
        IReadOnlyList<PortfolioPositionLot> positions,
        IReadOnlyList<PortfolioReservation> reservations,
        FxConversionSnapshot conversion);
}
```

### 15.3 Limits

Support:

- gross currency exposure;
- net currency exposure;
- risk-to-stop contribution by currency;
- configurable currency groups.

Missing conversion must reject a new order in shared portfolio mode.

---

## 16. Implement rolling correlation clusters

### 16.1 Input

Use completed returns from a configurable portfolio-risk interval, initially `1h`.

Use trailing data only.

### 16.2 Options

```csharp
public sealed record CorrelationRiskOptions
{
    public BarInterval Interval { get; init; } = BarInterval.Hours(1);
    public int LookbackBars { get; init; } = 120;
    public int MinimumSamples { get; init; } = 60;
    public decimal SoftCorrelationThreshold { get; init; } = 0.50m;
    public decimal HardCorrelationThreshold { get; init; } = 0.75m;
    public decimal SoftRiskMultiplier { get; init; } = 0.70m;
    public decimal HardRiskMultiplier { get; init; } = 0.40m;
}
```

### 16.3 Behaviour

- below soft threshold: no penalty;
- between soft and hard: reduce incremental risk;
- above hard: treat as same risk cluster;
- insufficient data: apply conservative configurable fallback, not zero correlation.

Calculate signed exposure relevance. A negatively correlated hedge should not be treated the same as an additional same-direction exposure, but hedging credit should be capped.

### 16.4 Determinism

Use stable instrument sorting and a deterministic clustering method. Do not use random initialisation.

---

## 17. Implement capital allocator and opportunity ranking

### 17.1 Purpose

When several valid signals compete for limited risk/margin, allocate to the best opportunity rather than first arrival.

### 17.2 Candidate

```csharp
public sealed record PortfolioOpportunity
{
    public required string StrategyId { get; init; }
    public required AgentDecision Decision { get; init; }
    public required PositionSizingResult Sizing { get; init; }
    public required decimal SetupQuality { get; init; }
    public required decimal ExpectedRewardRisk { get; init; }
    public required decimal RegimeSuitability { get; init; }
    public required decimal TransactionCostPenalty { get; init; }
    public required decimal CorrelationPenalty { get; init; }
    public required decimal CurrencyConcentrationPenalty { get; init; }
    public required decimal MarginConsumptionPenalty { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required long Sequence { get; init; }
}
```

### 17.3 Ranking score

Initial deterministic form:

```text
score =
    calibratedSetupQualityWeight
  + rewardRiskWeight
  + regimeSuitabilityWeight
  - transactionCostPenalty
  - correlationPenalty
  - currencyConcentrationPenalty
  - marginConsumptionPenalty
```

Do not treat raw confidence as calibrated probability.

Tie-breaking:

1. higher score;
2. lower requested risk;
3. earlier decision time;
4. strategy ID ordinal;
5. decision ID ordinal.

### 17.4 Admission window

At each frame:

1. collect all new opportunities from strategies;
2. rank them;
3. reserve risk/margin in order;
4. approve, resize, or reject;
5. submit approved orders effective on the next execution frame.

Do not submit strategy orders independently before portfolio ranking completes.

### 17.5 Resize

The allocator may reduce quantity to fit remaining budget, but only if:

- final quantity meets broker minimum;
- expected costs remain acceptable;
- reward/risk remains valid;
- strategy allows partial allocation.

Record original and allocated quantity.

---

# PART D — EQUITY PROTECTION AND ADAPTIVE SIZING

## 18. Add whole-account and per-strategy high-watermarks

### 18.1 Existing daily lock is not enough

Keep daily controls, but add persistent high-watermarks.

### 18.2 Models

```csharp
public enum EquityProtectionAction
{
    None,
    PauseNewEntries,
    ReduceOpenPositions,
    FlattenAllPositions,
    ReduceFutureRisk
}
```

```csharp
public sealed record EquityProtectionTier
{
    public required string TierId { get; init; }
    public decimal? ActivationProfitAmount { get; init; }
    public decimal? ActivationProfitPercent { get; init; }
    public decimal? MaximumGivebackAmount { get; init; }
    public decimal? MaximumGivebackPercent { get; init; }
    public required EquityProtectionAction Action { get; init; }
    public decimal ReductionFraction { get; init; }
    public decimal FutureRiskMultiplier { get; init; } = 1m;
}
```

```csharp
public sealed record EquityHighWatermarkSnapshot
{
    public required decimal StartingEquity { get; init; }
    public required decimal CurrentEquity { get; init; }
    public required decimal PeakEquity { get; init; }
    public required decimal DrawdownFromPeak { get; init; }
    public required decimal DrawdownPercent { get; init; }
    public required decimal ProtectedFloor { get; init; }
    public required IReadOnlySet<string> ActivatedTierIds { get; init; }
}
```

### 18.3 Account and strategy scopes

Create independent controllers:

```text
AccountEquityProtectionController
StrategyEquityProtectionController
```

Strategy equity attribution in shared mode must include:

- realised P/L;
- unrealised P/L for strategy-owned lots;
- allocated commissions;
- allocated financing.

### 18.4 Actions

#### Pause new entries

Blocks only new entries. Existing positions continue management.

#### Reduce open positions

Generate reduce-only requests, ranked weakest first. Weakness may use:

- open R;
- regime mismatch;
- adverse structure;
- setup quality;
- execution cost;
- portfolio concentration.

#### Flatten all positions

Emergency action. Cancel pending entries first, then close positions deterministically.

#### Reduce future risk

Changes sizing multiplier until recovery.

### 18.5 Recovery

Do not restore full risk immediately after one profitable candle.

Options:

```csharp
public int RecoveryConfirmationBars { get; init; } = 3;
public decimal RecoveryEquityThresholdPercent { get; init; }
public bool RequireNewEquityHighForFullRecovery { get; init; }
```

### 18.6 Events

Add:

```text
AccountHighWatermarkUpdated
StrategyHighWatermarkUpdated
EquityProtectionTierActivated
EquityProtectionReductionRequested
EquityProtectionFlattenRequested
RiskMultiplierChanged
EquityProtectionRecovered
```

---

## 19. Add drawdown-, volatility-, liquidity- and correlation-adaptive sizing

### 19.1 Do not replace base sizing

Keep `PositionSizer` responsible for raw quantity from fixed/fractional/cash risk.

Add a risk-budget policy before quantity calculation:

```csharp
public interface IRiskBudgetPolicy
{
    RiskBudgetDecision Evaluate(RiskBudgetContext context);
}
```

### 19.2 Multipliers

```text
finalRiskBudget =
    baseRiskBudget
  × drawdownMultiplier
  × volatilityMultiplier
  × regimeMultiplier
  × liquidityMultiplier
  × correlationMultiplier
  × strategyAllocationMultiplier
```

Every multiplier and final capped result must be logged.

### 19.3 Drawdown schedule

Configurable example:

```text
drawdown < 2%      → 1.00
2% to < 4%         → 0.75
4% to < 6%         → 0.50
6% or greater      → 0.00
```

### 19.4 Volatility schedule

Use ATR percentile or realised-volatility percentile:

```text
normal             → 1.00
high               → 0.70
extreme            → 0.30 or reject
```

### 19.5 Liquidity schedule

Use spread/ATR and session quality:

```text
good               → 1.00
degraded           → 0.50
unsafe             → 0.00
```

### 19.6 Caps

```csharp
public decimal MinimumCombinedRiskMultiplier { get; init; } = 0m;
public decimal MaximumCombinedRiskMultiplier { get; init; } = 1m;
```

Do not allow combined multipliers above `1` in the first production implementation. Increasing risk above the configured base requires later evidence and explicit opt-in.

---

## 20. Calibrate setup quality before using it for size

### 20.1 Raw confidence is not probability

Create exported calibration artefacts from `QuantResearch`.

```csharp
public sealed record SetupCalibrationBucket
{
    public required string StrategyId { get; init; }
    public required string InstrumentGroup { get; init; }
    public required string Regime { get; init; }
    public required decimal ConfidenceFrom { get; init; }
    public required decimal ConfidenceTo { get; init; }
    public required int Samples { get; init; }
    public required decimal WinRate { get; init; }
    public required decimal AverageR { get; init; }
    public required decimal ExpectedR { get; init; }
    public required decimal BrierScore { get; init; }
}
```

### 20.2 Runtime use

Runtime may:

- reject a bucket with negative expected R and sufficient samples;
- apply a risk reduction to weak but positive buckets;
- use normal base risk for strong validated buckets.

Initial implementation must not increase above base risk.

### 20.3 Versioning

Calibration file must include:

- schema version;
- training date range;
- instruments;
- strategy version;
- feature schema hash;
- parameters;
- sample counts;
- creation timestamp;
- data hash.

Reject incompatible calibration with an explicit diagnostic.

---

# PART E — TRADE-MANAGEMENT CALIBRATION AND REGIME PROFILES

## 21. Add MAE/MFE cohort analysis

### 21.1 Existing trade fields

The simulator already tracks MFE and MAE. Extend analysis, not fill logic.

### 21.2 Cohorts

Group by:

- strategy;
- instrument/instrument class;
- regime at entry;
- setup type/reason code;
- direction;
- session;
- volatility bucket;
- confidence bucket.

### 21.3 Metrics

For winners and losers:

- MFE at 1, 3, 5, 8, 12 bars;
- MAE before first +0.5R;
- time to +0.5R, +1R, +2R;
- maximum giveback after each MFE threshold;
- duration;
- exit efficiency;
- partial-exit contribution;
- runner contribution;
- stop distance distribution.

### 21.4 Export

```csharp
public sealed record TradeManagementCalibration
{
    public required string CalibrationId { get; init; }
    public required IReadOnlyList<TradeManagementCohort> Cohorts { get; init; }
    public required string SourceDataHash { get; init; }
}
```

### 21.5 Runtime application

Use conservative quantiles, not sample means alone.

Example:

```text
If 80% of validated winning trend-pullback trades reach +1R within 10 bars,
and expectancy after 14 bars without +0.5R is negative,
set stagnation review near 14 bars for that cohort.
```

Fallback to static defaults when sample count is insufficient.

---

## 22. Implement regime-dependent management profiles

### 22.1 Profile registry

```csharp
public sealed record PositionManagementProfile
{
    public required string ProfileId { get; init; }
    public required PositionManagementOptions Options { get; init; }
    public required IReadOnlySet<MarketRegime> ApplicableRegimes { get; init; }
}
```

### 22.2 Profile examples

#### Trend

- smaller early reduction;
- larger runner;
- wider structural ATR buffer;
- larger allowed MFE giveback;
- thesis exit on higher timeframe.

#### Range

- earlier reduction;
- target near opposite range boundary;
- small runner or no runner;
- tighter stagnation;
- reject midpoint management changes based on weak structure.

#### Breakout expansion

- fast break-even only after price clears costs and a minimum R;
- avoid immediate tight swing trailing;
- allow retest;
- then use structure trail.

#### Disorder

- no new entries;
- reduce current exposure;
- tighten account safeguards.

### 22.3 Profile switching after entry

Do not blindly replace all rules when regime changes.

Record:

- entry regime;
- current regime;
- selected profile;
- switch reason.

Safe policy:

- management may become more protective when regime degrades;
- management must not widen an existing stop;
- completed scale-out stages remain completed;
- runner minimum may reduce only through an explicit risk/safety action;
- a regime improvement cannot restore already reduced quantity.

---

# PART F — REALISTIC EXECUTION AND COSTS

## 23. Add execution model abstraction

### 23.1 Interface

```csharp
public interface ISimulationExecutionModel
{
    FillEvaluation Evaluate(
        SimulatedOrder order,
        MarketCandle candle,
        ExecutionModelContext context);
}
```

### 23.2 Models

Support:

```csharp
public enum SimulationFillModel
{
    MidpointPlusConfiguredSpread,
    HistoricalBidAsk,
    VariableSyntheticSpread,
    StressExecution
}
```

### 23.3 Variable synthetic spread

When historical bid/ask is unavailable:

```text
spread =
    baseSpread
  × sessionMultiplier
  × volatilityMultiplier
  × eventMultiplier
  × rolloverMultiplier
```

Persist every multiplier.

### 23.4 Slippage

Use directionally adverse slippage by default.

Components:

- base slippage;
- ATR/volatility component;
- gap component;
- order-size/liquidity component;
- stress multiplier.

Never apply favourable slippage by default.

### 23.5 Gap-through-stop

When candle opens beyond a stop:

- fill at adverse executable open plus slippage;
- do not fill at stop price;
- record `GapThroughStop`.

### 23.6 Partial fills

Initial support:

```csharp
public sealed record FillCapacityModel
{
    public decimal MaximumQuantityPerExecutionFrame { get; init; }
    public decimal MaximumParticipationFraction { get; init; }
}
```

Without reliable volume, use configured deterministic capacity and label it synthetic.

Protective orders must remain for unfilled quantity.

### 23.7 Rejected operations

Allow deterministic fault scenarios:

- stop amendment rejected;
- close rejected;
- connection unavailable;
- rate limit;
- stale quote.

Use seeded scenario plans in tests.

---

## 24. Add financing and rollover model

### 24.1 Interface

```csharp
public interface IFinancingModel
{
    FinancingCharge Calculate(
        PortfolioPositionLot position,
        DateTimeOffset from,
        DateTimeOffset to,
        FinancingContext context);
}
```

### 24.2 Required support

- long rate;
- short rate;
- broker timezone/rollover instant;
- triple financing day;
- holiday adjustment;
- account-currency conversion;
- per-strategy allocation.

### 24.3 Ledger

Add:

```csharp
LedgerEntryType.Financing
```

Trade records require:

```text
TotalFinancing
NetProfitAfterFinancing
```

### 24.4 Data honesty

When broker financing history is unavailable, use a versioned configured model and mark it synthetic.

---

## 25. Add stress execution scenarios

Support named scenarios:

```text
Base
SpreadDouble
SlippageTriple
GapStress
StopAmendmentFailure
ConnectionLoss
CorrelationShock
CombinedStress
```

Run the same signals under the same market data with only the execution/risk scenario changed.

Report:

- net P/L;
- max drawdown;
- stop slippage;
- rejected amendments;
- margin stress;
- number of trades reversed by execution degradation.

---

# PART G — RESEARCH AND VALIDATION FRAMEWORK

## 26. Walk-forward testing

### 26.1 Structure

```text
warm-up
training/calibration
validation
test
roll window forward
```

The untouched test segment must not influence parameter selection.

### 26.2 API

```csharp
public sealed record WalkForwardPlan
{
    public required TimeSpan TrainingWindow { get; init; }
    public required TimeSpan ValidationWindow { get; init; }
    public required TimeSpan TestWindow { get; init; }
    public required TimeSpan Step { get; init; }
    public required TimeSpan PurgeGap { get; init; }
}
```

### 26.3 Output

Per fold and aggregate:

- net P/L;
- average R;
- profit factor;
- drawdown;
- Sharpe/Sortino where sampling is valid;
- trade count;
- regime distribution;
- parameter selection;
- degradation from validation to test.

---

## 27. Purged time-series cross-validation

For overlapping labels or holding periods, purge samples near fold boundaries.

Do not randomly shuffle time-series trades.

Embargo should be configurable.

---

## 28. Monte Carlo

Support:

1. trade-order bootstrap;
2. block bootstrap to preserve local dependence;
3. slippage/spread perturbation;
4. missed-trade simulation;
5. clustered-loss stress.

Report distributions:

- final equity;
- max drawdown;
- longest loss sequence;
- probability of breaching safety threshold;
- time to recovery.

Use deterministic seeds recorded in output.

---

## 29. Feature ablation

Define feature switches for:

- price action;
- ADX/DMI;
- RSI relationship;
- Bollinger context;
- regime routing;
- secondary trend;
- setup intervals;
- currency strength;
- session filter;
- scale-out;
- profit floor;
- MFE giveback;
- structural trailing;
- adaptive sizing.

Run identical periods and seeds.

Produce delta metrics and reject components that only improve in-sample.

---

## 30. Parameter sensitivity

Do not optimise one exact best parameter.

Generate surfaces for:

- risk percentage;
- regime thresholds;
- ADX threshold;
- ER threshold;
- scale-out R;
- profit floors;
- MFE giveback;
- ATR buffers;
- stagnation bars;
- spread/ATR limits.

Prefer broad stable regions.

---

## 31. Confidence calibration

Produce:

- reliability diagram data;
- actual win rate by predicted bucket;
- expected R by bucket;
- Brier score;
- calibration error;
- sample count.

Do not label raw confidence as probability before calibration.

---

# PART H — OPTIONAL ML AFTER THE DETERMINISTIC SYSTEM IS VALIDATED

## 32. Meta-labelling only as first ML production model

### 32.1 Purpose

The rule-based Agent creates a valid candidate. ML decides:

```text
trade / do not trade
```

It must not invent orders or bypass risk controls.

### 32.2 Features

Use only information available at decision time:

- regime;
- multi-timeframe alignment;
- setup confidence;
- price-action events;
- structure distances;
- ATR/ADX/ER/Bollinger;
- Donchian context;
- value-reference distance;
- session/spread;
- currency strength;
- proposed R:R;
- planned costs;
- portfolio concentration.

### 32.3 Label

Example triple-barrier label:

- target hit before stop within maximum horizon;
- stop hit first;
- timed out.

Resolve same-candle ambiguity using the existing configured fill policy or sub-minute data.

### 32.4 Validation

- purged walk-forward;
- probability calibration;
- feature importance stability;
- drift monitoring;
- minimum sample thresholds.

### 32.5 Runtime contract

```csharp
public interface ISetupMetaModel
{
    MetaLabelDecision Evaluate(MetaLabelFeatures features);
}
```

Output:

```csharp
public sealed record MetaLabelDecision
{
    public required bool Trade { get; init; }
    public required decimal Probability { get; init; }
    public required string ModelVersion { get; init; }
    public required string ReasonCode { get; init; }
}
```

ML may reject or reduce risk. Initial production version must not increase above base risk.

---

## 33. Optional unsupervised regime clustering

Use only in research first.

Compare clusters with the deterministic regime classifier. Do not replace explainable production regimes until clusters are stable across folds and instruments.

---

# PART I — API, DASHBOARD, CLI, PERSISTENCE AND OBSERVABILITY

## 34. Configuration additions

Add grouped options:

```csharp
public sealed record QuantitativeEnhancementOptions
{
    public MarketRegimeOptions Regime { get; init; } = new();
    public TradingConditionOptions Conditions { get; init; } = new();
    public PortfolioRiskOptions PortfolioRisk { get; init; } = new();
    public CorrelationRiskOptions Correlation { get; init; } = new();
    public EquityProtectionOptions EquityProtection { get; init; } = new();
    public AdaptiveRiskOptions AdaptiveRisk { get; init; } = new();
    public ExecutionModelOptions Execution { get; init; } = new();
    public FinancingOptions Financing { get; init; } = new();
}
```

Add to `BacktestRuntimeOptions` or a clearly named sibling, and include every field in:

- request mapping;
- validation;
- simulation configuration identity;
- manifest;
- persisted job envelope;
- CLI;
- Dashboard form;
- replay metadata.

---

## 35. Dashboard design

Add sections:

### Market regime

- enabled;
- ER period;
- regime confirmation bars;
- spread/ATR limits;
- show current/entry regime.

### Trading conditions

- allowed sessions;
- rollover blackout;
- spread thresholds;
- event blackout;
- degraded-data policy.

### Portfolio mode

- independent/shared;
- total heat limit;
- strategy/instrument/currency limits;
- margin limit;
- reserve;
- maximum positions;
- correlation interval/lookback/thresholds.

### Equity protection

- account tiers;
- strategy tiers;
- action;
- reduction fraction;
- future-risk multiplier;
- recovery rules.

### Adaptive sizing

- drawdown schedule;
- volatility schedule;
- minimum multiplier;
- calibrated setup policy.

### Execution

- fill model;
- variable spread;
- slippage;
- partial-fill capacity;
- financing;
- stress scenario.

Use explicit units in labels:

- `% of equity`;
- account currency;
- ATR multiples;
- bars;
- basis points;
- quantity units.

Validate relationships before POST.

---

## 36. Open-position and portfolio diagnostics

Display:

```text
Current regime
Entry regime
Regime confidence
Base risk budget
Final risk budget
Every risk multiplier
Raw quantity
Allocated quantity
Planned stop risk
Total portfolio heat
Strategy heat
Instrument heat
Currency exposures
Correlation cluster
Margin used
Reserved margin
Unallocated reserve
Account peak equity
Strategy peak equity
Protected floors
Activated protection tiers
```

---

## 37. Replay event additions

Add at minimum:

```text
MarketRegimeChanged
MarketRegimeConfirmed
TradingConditionEvaluated
TradingConditionRejected
RiskBudgetAdjusted
PortfolioOpportunityCreated
PortfolioOpportunityRanked
PortfolioRiskReserved
PortfolioRiskReservationRejected
PortfolioRiskReservationReleased
PortfolioQuantityReduced
CurrencyExposureLimitReached
CorrelationPenaltyApplied
AccountHighWatermarkUpdated
StrategyHighWatermarkUpdated
EquityProtectionTierActivated
EquityProtectionReductionRequested
EquityProtectionFlattenRequested
EquityProtectionRecovered
FinancingCharged
ExecutionSpreadAdjusted
ExecutionSlippageApplied
GapThroughStop
PartialFill
OperationFaultInjected
MetaLabelEvaluated
MetaLabelRejected
```

Every event requires:

- strategy ID where applicable;
- position/order/decision/reservation IDs;
- sequence;
- event time;
- reason code;
- previous/new values;
- option/profile/model version.

---

## 38. Performance output additions

### Portfolio

- total and peak heat;
- average margin use;
- peak margin use;
- rejected opportunities;
- resized opportunities;
- opportunity cost;
- concentration by currency;
- concentration by cluster;
- account high-watermark giveback;
- protection activations.

### Strategy attribution

- allocated capital;
- realised/unrealised P/L;
- financing;
- commissions;
- contribution to drawdown;
- contribution to portfolio heat;
- rejected/resized signals;
- expectancy by regime.

### Execution

- average spread;
- average slippage;
- stop slippage;
- gap fills;
- partial fills;
- rejected amendments;
- financing total.

### Regime

- time spent in each regime;
- trades per regime;
- expectancy per regime;
- drawdown per regime;
- regime transition performance.

---

# PART J — IMPLEMENTATION SEQUENCE

## 39. Mandatory phased delivery

Do not attempt all features in one unreviewable commit.

### Phase 1 — deterministic market features and regime classifier

- Efficiency Ratio;
- Donchian;
- value reference;
- regime classifier;
- snapshot/replay/chart;
- strategy routing;
- unit and no-lookahead tests.

### Phase 2 — trading-condition filter

- sessions;
- rollover;
- spread/ATR;
- event-provider abstraction;
- Agent/Risk integration;
- diagnostics.

### Phase 3 — PortfolioManager and shared account

- reservation book;
- portfolio heat;
- margin reserve;
- strategy/instrument/currency limits;
- deterministic ranking;
- shared account simulation;
- attribution.

### Phase 4 — high-watermarks and adaptive sizing

- account/strategy controllers;
- tier actions;
- recovery;
- adaptive risk multipliers;
- Dashboard and replay.

### Phase 5 — calibrated management

- MAE/MFE reports;
- management calibration artefacts;
- regime management profiles;
- static fallback.

### Phase 6 — realistic execution

- execution abstraction;
- variable spread;
- slippage/gaps;
- partial fills;
- financing;
- stress scenarios.

### Phase 7 — quantitative research framework

- walk-forward;
- purging/embargo;
- Monte Carlo;
- ablation;
- sensitivity;
- confidence calibration.

### Phase 8 — cross-market context and optional ML

- currency strength;
- meta-labelling;
- drift/version controls.

Each phase must compile and pass all existing tests before proceeding.

---

# PART K — TESTING REQUIREMENTS

## 40. Unit tests

Test every formula and state machine independently.

Required examples:

- ER batch equivalence;
- Donchian previous-boundary breakout;
- value-anchor reset;
- regime hysteresis;
- session boundary and DST;
- spread/ATR filter;
- risk reservation atomicity;
- portfolio heat after stop movement;
- currency decomposition;
- correlation penalty;
- allocator tie-breaking;
- high-watermark activation/recovery;
- adaptive multiplier caps;
- gap-through-stop;
- financing triple day;
- calibration schema compatibility.

---

## 41. Property and invariant tests

Add tests asserting:

- quantity never exceeds risk budget after rounding;
- reservation totals never exceed configured limits;
- releasing twice does not alter balances twice;
- total allocated quantity never exceeds filled quantity;
- stop risk never increases after protective amendment;
- protective quantities always equal remaining position;
- equity protection never opens a position;
- risk multipliers never increase beyond configured cap;
- no future candle affects current features;
- independent mode results remain unchanged when portfolio features are disabled;
- sequential and parallel strategy modes produce identical results.

---

## 42. Integration tests

Create deterministic inline candle scenarios for:

1. compression → breakout → trend;
2. range boundary trade;
3. high-volatility disorder rejection;
4. two strategies competing for limited risk;
5. correlated pair rejection/resizing;
6. currency concentration limit;
7. account high-watermark partial reduction;
8. strategy-only pause while other strategy continues;
9. stop gap and slippage;
10. financing across rollover;
11. saved job reload with new schema;
12. Dashboard request round-trip.

---

## 43. Regression tests

Capture baseline hashes for:

- current independent Legacy;
- current independent Improved;
- current BrokerAssetCatalog;
- existing profit protection.

With all new features disabled or default-compatible, baseline results must remain unchanged unless a documented bug fix intentionally changes them.

---

## 44. Performance tests

Measure:

- feature-update microseconds per candle;
- regime-classification microseconds;
- portfolio allocation time per frame;
- memory use for 1, 10 and 50 instruments;
- shared-mode throughput;
- replay output growth.

Use bounded buffers and incremental calculations.

---

# PART L — ACCEPTANCE CRITERIA

## 45. Phase-level acceptance

A phase is complete only when:

- implementation is connected to the real execution path;
- Dashboard/API/CLI configuration works;
- configuration identity includes new behaviour;
- replay and performance diagnostics exist;
- unit and integration tests pass;
- no-lookahead tests pass;
- sequential and parallel outputs match;
- README and architecture docs are updated;
- no credentials/build artefacts are packaged.

---

## 46. Final system acceptance

The complete implementation must demonstrate:

1. the same valid setup receives different risk/management treatment in trend versus range;
2. poor spread/session conditions can delay, resize or reject entry;
3. two simultaneous strategies cannot exceed shared heat or margin limits;
4. correlated/currency-concentrated positions are penalised or rejected;
5. capital is reserved for pending orders and released exactly once;
6. account and strategy high-watermarks can pause, reduce or flatten according to policy;
7. position risk decreases during drawdown/volatility stress;
8. MAE/MFE calibration can alter management only through a versioned artefact;
9. realistic spread/slippage/financing reduce results transparently;
10. walk-forward and ablation reports identify whether each feature adds out-of-sample value;
11. all decisions are explainable through reason-coded events;
12. independent strategy mode remains backward compatible.

---

# PART M — IMPLEMENTER RULES

## 47. Instructions to the AI coding agent

1. Read all current architecture and validation documents before editing.
2. Run baseline tests and save results.
3. Do not duplicate existing managers.
4. Keep PortfolioManager, RiskManager, TradeManager and ExecutionManager responsibilities separate.
5. Prefer immutable records at boundaries.
6. Use interfaces for external data and execution models.
7. Use decimal for prices, quantities and money.
8. Use UTC/DateTimeOffset.
9. Use bounded collections.
10. Never catch and silently ignore trading-critical exceptions.
11. Use explicit reason codes for every decision.
12. Do not introduce optimistic defaults.
13. Do not use current/future evaluation outcomes for runtime calibration.
14. Do not implement fake order-flow indicators.
15. Do not allow ML to bypass deterministic risk controls.
16. Update solution references and tests with each new project.
17. Keep each phase reviewable.
18. Package source only, excluding `bin`, `obj`, `node_modules`, `dist`, `.cache`, credentials and generated simulations.

---

## 48. Required final deliverables

The implementing AI must provide:

- updated source ZIP;
- checksum;
- migration/upgrade notes;
- architecture diagram;
- file-by-file change summary;
- configuration examples;
- test report;
- baseline versus enhanced comparison;
- known limitations;
- demo-account certification checklist;
- explicit statement of any test that could not be run.

---

## 49. Recommended initial safe defaults

These are research defaults, not guaranteed optimal settings.

```text
Simulation account mode:
    IndependentStrategyAccounts

Base risk per trade:
    0.25% equity

Maximum total portfolio heat:
    1.50%

Maximum strategy heat:
    0.75%

Maximum instrument heat:
    0.75%

Maximum currency heat:
    0.75%

Maximum margin usage:
    30%

Minimum unallocated margin reserve:
    30%

Maximum open positions:
    3

Correlation:
    1h returns
    120-bar lookback
    60 minimum samples
    soft 0.50
    hard 0.75

Regime:
    2 confirmation bars
    3 persistence bars
    unsafe spread/ATR hard limit 0.30

Adaptive risk:
    never above 1.0 × base risk

ML:
    disabled

Economic-event provider:
    disabled/unavailable unless a versioned provider is configured

Historical execution:
    clearly identify synthetic or historical bid/ask
```

Before live trading, validate against:

- several instruments;
- several regimes;
- multiple years;
- walk-forward folds;
- stress execution;
- broker demo environment.

---

## 50. Architectural target

```text
Completed market data
    ↓
ChartAnnotationEngine
    ├── Existing indicators and price action
    ├── Efficiency Ratio
    ├── Donchian
    ├── Anchored value references
    └── MarketRegimeClassifier
            ↓
Progressive Agent
    ├── MTF evidence
    ├── Regime policy
    ├── Trading-condition filter
    └── Candidate decision
            ↓
RiskBudgetPolicy
    ├── Drawdown
    ├── Volatility
    ├── Liquidity
    ├── Regime
    └── Calibration
            ↓
PositionSizer
            ↓
PortfolioManager
    ├── Opportunity ranking
    ├── Risk/margin reservation
    ├── Portfolio heat
    ├── Currency exposure
    └── Correlation clusters
            ↓
PreTradeRiskManager
            ↓
ExecutionManager
            ↓
Broker / SimulatedBroker
            ↓
TradeManager
    ├── Mechanical protection
    ├── Fast structure
    ├── Main structure
    ├── Thesis management
    └── Regime-specific profile
            ↓
Account and strategy equity protection
            ↓
Journal / Replay / Performance / Dashboard
            ↓
QuantResearch
    ├── Walk-forward
    ├── Monte Carlo
    ├── Ablation
    ├── Sensitivity
    ├── Calibration
    └── Optional meta-labelling
```

This architecture should be the authoritative implementation target.
