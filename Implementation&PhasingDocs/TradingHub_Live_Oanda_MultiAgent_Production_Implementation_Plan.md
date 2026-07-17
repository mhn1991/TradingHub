# TradingHub Live OANDA Multi-Agent Platform  
## Production Implementation Plan

**Target repository:** current `TradingHub_source(1).zip`  
**Target solution:** `TradingHub.slnx`  
**Primary broker for this phase:** OANDA Practice/Demo  
**Primary goal:** run multiple isolated Agent instances over a configured list of markets, centrally allocate account risk, place protected demo orders, reconcile every broker event, and observe the complete system in real time.

---

# 1. Purpose

This plan defines the implementation of a modular live-trading subsystem that connects the existing TradingHub components:

```text
OANDA real-time data
    → ChartAnnotator
    → multi-timeframe Agent
    → setup calibration
    → statistical meta-label model
    → adaptive position sizing
    → portfolio risk and allocation
    → ExecutionManager
    → OANDA demo orders
    → transaction reconciliation
    → TradeManager
    → monitoring, persistence and safety
```

The simulator remains the historical research engine. It does not need to become a perfect model of live execution before OANDA demo testing begins.

The OANDA demo environment is used to validate:

- real-time market-data timing;
- candle completion;
- multi-market concurrency;
- Agent signal generation;
- feature-policy behaviour;
- calibration and meta-label application;
- position sizing;
- portfolio competition;
- broker order mapping;
- initial protective stops;
- fills, rejects and cancellations;
- partial closing;
- stop replacement;
- restart recovery;
- transaction reconciliation;
- live trade management;
- account-level safety;
- operational monitoring.

The demo account is not treated as proof of strategy profitability. Historical walk-forward and untouched testing remain necessary for that purpose.

---

# 2. Preconditions from §§14–17

This live implementation assumes the following production work is complete and tested:

1. `FeatureSwitchMapper` controls all fourteen supported switches.
2. `RsiBollingerSignals` and DMI confirmation are wired through runtime configuration.
3. `QuantResearchRunner` can run real historical backtests.
4. Walk-forward, Monte Carlo, ablation and sensitivity commands are operational.
5. Setup and management calibration artifacts can be generated.
6. Detailed excursion tracking is optional and disabled by default.
7. `ICalibrationArtifactRepository` stores artifacts using server-owned IDs.
8. Dashboard and CLI resolve artifacts by ID, never by arbitrary client paths.
9. `CalibratedSetupMetaModel` is implemented.
10. The meta-model is actually passed to `StreamingComparativeEngineOptions.MetaLabelModel`.
11. `SetupCalibrationPolicy` validates the required feature schema hash.
12. Meta-label risk multipliers are always within `0..1`.
13. A missing or insufficient meta bucket is neutral rather than automatically rejecting.
14. The full .NET and Dashboard test suites pass.

Do not begin autonomous OANDA execution while any of these conditions is unverified.

---

# 3. Core architectural rules

## 3.1 One authoritative broker account

There must be one authoritative OANDA account state for the live engine:

```text
one balance
one equity
one margin pool
one order inventory
one position inventory
one account safety state
```

Agents do not own separate accounts.

## 3.2 Agents produce intent only

An Agent may produce:

```text
Observe
Buy candidate
Sell candidate
Close candidate
Cancel candidate
```

An Agent must never:

- call OANDA directly;
- reserve capital directly;
- calculate account-wide portfolio admission;
- retry an uncertain order;
- mutate another Agent's state.

## 3.3 Central admission

Every opening candidate must pass through one serialized portfolio decision point:

```text
candidate
    → setup/meta evaluation
    → trading-condition filter
    → risk budget
    → position sizing
    → portfolio allocation
    → broker execution
```

## 3.4 Broker-side protection

Every autonomous entry must have a broker-side initial stop attached atomically through `stopLossOnFill`.

No autonomous entry may create an unprotected broker position.

## 3.5 Completed-candle analysis

The strategic Agent uses completed candles only.

Live quotes may be used for:

- executable price;
- spread;
- current P/L;
- MFE/MAE;
- emergency account protection;
- order eligibility.

They must not create unfinished strategic indicators.

## 3.6 Fault isolation

A failure in one market actor must not crash unrelated market actors.

A broker/account uncertainty must pause all new entries because account state is shared.

## 3.7 Explicit operating modes

```csharp
public enum LiveAgentMode
{
    ObserveOnly,
    Shadow,
    ManualApproval,
    AutonomousDemo
}
```

No mode may be inferred silently from environment variables.

## 3.8 Demo-only first release

The first production release must refuse to start autonomous execution when:

```text
OANDA environment != Practice/Demo
```

Live-account support requires a later explicit certification phase.

---

# 4. Recommended new projects

Create these projects:

```text
Calibration/
LiveTrading/
LiveTrading.Oanda/
LiveTradingHost/
LiveTrading.Tests/
```

## 4.1 `Calibration`

Purpose:

- shared calibration artifact contracts;
- server-owned repository interface;
- file repository;
- compatibility validation;
- artifact metadata;
- promotion status.

Move or refactor the existing `Simulator/Calibration` implementation so that live trading does not reference `Simulator`.

Dependencies:

```text
Calibration
    → Agent
    → RiskManager
    → TradeManager
```

Avoid any reverse reference from those projects back to `Calibration`.

## 4.2 `LiveTrading`

Broker-neutral live trading application layer.

Owns:

- engine lifecycle;
- market universe;
- market actors;
- Agent supervisor;
- candidate epoch coordination;
- portfolio decisions;
- registry;
- reconciliation contracts;
- live management;
- safety;
- persistence contracts;
- monitoring snapshots.

Dependencies:

```text
LiveTrading
    → TradingCore
    → Agent
    → ChartAnnotator
    → Brokers
    → RiskManager
    → PortfolioManager
    → ExecutionManager
    → TradeManager
    → TradingJournal
    → Calibration
```

## 4.3 `LiveTrading.Oanda`

OANDA-specific adapter.

Owns:

- multi-instrument pricing stream adapter;
- REST completed-candle provider;
- transaction stream adapter;
- OANDA account reconciliation;
- stop replacement;
- partial-close implementation;
- OANDA capability certification.

Dependencies:

```text
LiveTrading.Oanda
    → LiveTrading
    → Brokers
```

Do not put general engine state machines here.

## 4.4 `LiveTradingHost`

.NET Worker/Web host.

Owns:

- dependency injection;
- configuration;
- hosted service;
- API;
- SignalR;
- account ownership lock;
- health checks;
- process lifecycle;
- authentication for remote control.

Dependencies:

```text
LiveTradingHost
    → LiveTrading
    → LiveTrading.Oanda
    → DashboardContracts
```

## 4.5 `LiveTrading.Tests`

Owns:

- unit tests;
- deterministic integration fixtures;
- fake broker;
- fault injection;
- restart tests;
- concurrency tests;
- OANDA demo tests marked explicit/integration.

---

# 5. Dependency direction

The authoritative dependency graph must remain acyclic:

```text
Agent / ChartAnnotator / RiskManager / TradeManager / PortfolioManager
                         ↑
                    TradingCore
                         ↑
                    LiveTrading
                         ↑
                LiveTrading.Oanda
                         ↑
                  LiveTradingHost
```

Offline research remains separate:

```text
QuantResearchRunner
    → Simulator
    → Calibration
```

`LiveTrading` must not reference:

```text
Simulator
QuantResearch
QuantResearchRunner
DashboardLive
```

The live runtime consumes artifact contracts and runtime policies, not the research engine.

---

# 6. Shared runtime policy

Create one immutable deployment bundle.

```csharp
public sealed record LiveTradingPolicyBundle
{
    public required string PolicyBundleId { get; init; }

    public required int Revision { get; init; }

    public required string StrategyVersion { get; init; }

    public required string FeatureSchemaHash { get; init; }

    public required RuntimeFeaturePolicy FeaturePolicy { get; init; }

    public Guid? SetupCalibrationArtifactId { get; init; }

    public Guid? ManagementCalibrationArtifactId { get; init; }

    public Guid? MetaModelArtifactId { get; init; }

    public required PositionSizingOptions PositionSizing { get; init; }

    public required AdaptiveRiskOptions AdaptiveRisk { get; init; }

    public required PortfolioRiskOptions PortfolioRisk { get; init; }

    public required TradingConditionOptions TradingConditions { get; init; }

    public required TradingSafetyOptions AccountSafety { get; init; }

    public required PositionManagementOptions LegacyManagement { get; init; }

    public required PositionManagementOptions ImprovedManagement { get; init; }

    public required string ConfigurationHash { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? Description { get; init; }
}
```

## 6.1 Validation

Before starting, validate:

- artifact IDs exist;
- artifact types match;
- content hashes match;
- schema versions match;
- feature schema hashes match;
- strategy IDs match;
- timeframe plan is compatible;
- meta-model multiplier cannot exceed one;
- autonomous mode is demo-only;
- risk and margin limits are positive;
- at least one market and one Agent are enabled;
- every Agent has all required timeframes;
- initial stop is mandatory;
- no unsupported live-management feature is enabled.

## 6.2 Immutability

The active bundle is immutable.

Changing any behaviour requires:

```text
create new policy revision
    → validate
    → pause entries
    → activate at controlled boundary
    → journal activation
```

Do not mutate settings in place while orders are being admitted.

---

# 7. Shared strategy pipeline factory

The simulator and live engine must construct the strategy decision path through the same factory.

Place it in `TradingCore`.

```csharp
public interface IStrategyDecisionPipelineFactory
{
    StrategyDecisionRuntime Create(
        StrategyRuntimeDefinition strategy,
        RuntimeFeaturePolicy featurePolicy,
        SetupCalibrationArtifact? setupCalibration,
        ISetupMetaModel? metaModel,
        TradingSafetyOptions safetyOptions);
}
```

```csharp
public sealed record StrategyDecisionRuntime
{
    public required ITradingAgent Agent { get; init; }

    public required SafeTradingPipeline Pipeline { get; init; }

    public required string StrategyVersion { get; init; }

    public required string FeaturePolicyHash { get; init; }

    public required string? SetupCalibrationId { get; init; }

    public required string? MetaModelVersion { get; init; }
}
```

Both must use it:

```text
BacktestApplicationService
LiveTradingSupervisor
```

This prevents simulator/live divergence and repeated dead-wiring mistakes.

---

# 8. Live engine lifecycle

Create:

```csharp
public enum LiveEngineState
{
    Stopped,
    Starting,
    AcquiringOwnership,
    ResolvingPolicy,
    ConnectingBroker,
    Reconciling,
    WarmingUp,
    ObserveOnly,
    ManualApproval,
    AutonomousDemo,
    Paused,
    Degraded,
    Flattening,
    Stopping,
    Faulted
}
```

```csharp
public interface ILiveTradingSupervisor
{
    LiveTradingSnapshot Snapshot { get; }

    Task StartAsync(
        LiveTradingPlan plan,
        CancellationToken cancellationToken);

    Task PauseNewEntriesAsync(
        string reason,
        CancellationToken cancellationToken);

    Task ResumeAsync(CancellationToken cancellationToken);

    Task SetExecutionModeAsync(
        LiveExecutionMode mode,
        CancellationToken cancellationToken);

    Task CancelPendingEntriesAsync(
        string reason,
        CancellationToken cancellationToken);

    Task FlattenAsync(
        string reason,
        CancellationToken cancellationToken);

    Task StopAsync(
        CancellationToken cancellationToken);
}
```

## 8.1 Startup order

```text
Acquire account ownership lock
    ↓
Load and validate policy bundle
    ↓
Load calibration artifacts
    ↓
Create shared pipeline factory outputs
    ↓
Connect OANDA REST
    ↓
Fetch account, positions and orders
    ↓
Connect transaction stream
    ↓
Reconcile broker and local state
    ↓
Connect multi-market pricing stream
    ↓
Warm all required timeframes
    ↓
Start market actors
    ↓
Start Agents in observe mode
    ↓
Allow requested execution mode only after health gate
```

## 8.2 Shutdown order

```text
Pause new entries
    ↓
Stop creating decision epochs
    ↓
Drain pending internal candidates
    ↓
Optionally cancel pending broker entries
    ↓
Keep broker protective stops intact
    ↓
Persist checkpoint
    ↓
Stop actors
    ↓
Stop streams
    ↓
Release ownership lock
```

Stopping the engine must not automatically flatten unless explicitly requested.

---

# 9. Account ownership lock

Only one process may control one OANDA account.

```csharp
public interface ITradingAccountLease
{
    Task<AccountLeaseResult> TryAcquireAsync(
        string broker,
        string accountId,
        string instanceId,
        CancellationToken cancellationToken);

    Task RenewAsync(CancellationToken cancellationToken);

    Task ReleaseAsync(CancellationToken cancellationToken);
}
```

Initial implementation:

- file lock plus lease metadata;
- machine/process ID;
- heartbeat timestamp;
- stale-lease recovery requiring explicit force flag.

Later distributed deployment may use a database lease.

If lease renewal fails:

```text
pause new entries
continue receiving broker events
raise critical alert
do not blindly release account ownership
```

---

# 10. Market universe

Create a configured universe service.

```csharp
public sealed record LiveMarketDefinition
{
    public required InstrumentKey Instrument { get; init; }

    public required IReadOnlyList<LiveStrategyAssignment> Strategies { get; init; }

    public required BarInterval ExecutionInterval { get; init; }

    public required BarInterval AnalysisBaseInterval { get; init; }

    public required IReadOnlySet<BarInterval> AnalysisIntervals { get; init; }

    public bool Enabled { get; init; } = true;

    public decimal? MaximumInstrumentRiskPercent { get; init; }

    public decimal? MaximumInstrumentMarginPercent { get; init; }

    public string? LiquidityProfileId { get; init; }
}
```

```csharp
public sealed record LiveStrategyAssignment
{
    public required string StrategyId { get; init; }

    public required LiveAgentMode Mode { get; init; }

    public required ProgressiveStrategyTimeframes Timeframes { get; init; }

    public bool Enabled { get; init; } = true;
}
```

Initial recommendation:

```text
EUR/USD
GBP/USD
USD/JPY
GBP/JPY
EUR/JPY
```

Use Improved as the only executable Agent initially.

Legacy runs shadow-only until it demonstrates positive out-of-sample performance.

---

# 11. Market-data subsystem

## 11.1 Design

Use one pricing stream containing every selected market.

Use a separate centralized completed-candle coordinator.

```text
OANDA pricing stream
    → quotes, spread, liveness

OANDA REST M1 candles
    → authoritative completed analysis candles
```

The first implementation should not build strategic candles solely from ticks.

## 11.2 Interfaces

```csharp
public interface ILiveQuoteStream
{
    IAsyncEnumerable<LiveQuoteEvent> ReadAsync(
        IReadOnlyList<InstrumentKey> instruments,
        CancellationToken cancellationToken);
}
```

```csharp
public interface ICompletedCandleProvider
{
    Task<IReadOnlyList<Candle>> GetCompletedCandlesAsync(
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset fromExclusive,
        DateTimeOffset toInclusive,
        CancellationToken cancellationToken);
}
```

```csharp
public interface ILiveMarketDataHub
{
    ChannelReader<LiveMarketEvent> Events { get; }

    bool TryGetLatestQuote(
        InstrumentKey instrument,
        out LiveQuoteSnapshot quote);

    MarketDataHealthSnapshot Health { get; }
}
```

## 11.3 Quote state

```csharp
public sealed record LiveQuoteSnapshot
{
    public required InstrumentKey Instrument { get; init; }

    public required decimal Bid { get; init; }

    public required decimal Ask { get; init; }

    public decimal Mid => (Bid + Ask) / 2m;

    public decimal Spread => Ask - Bid;

    public required DateTimeOffset BrokerTime { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public required bool IsTradeable { get; init; }

    public required bool IsStale { get; init; }
}
```

## 11.4 Candle scheduler

At every canonical M1 boundary:

1. wait a small configurable broker-finalization delay;
2. request completed candles since the last confirmed M1;
3. validate chronological continuity;
4. publish all missing completed candles in order;
5. update last confirmed close;
6. mark the market healthy.

Do not assume exactly one candle arrived.

## 11.5 Catch-up

After reconnect:

- pause entries for affected markets;
- fetch every missing M1 candle;
- process chronologically;
- rebuild higher intervals;
- do not emit historical duplicate decisions;
- re-enable only after current timestamp is reached.

## 11.6 Channels and backpressure

Use bounded channels.

Suggested channels:

```text
quotes:
    bounded, drop/coalesce older quote per instrument

completed candles:
    bounded, never drop

broker events:
    bounded, never drop

journal:
    bounded, never silently drop
```

No `Task.Run` per tick.

---

# 12. Market actor

Create one actor per instrument.

```csharp
public enum LiveMarketState
{
    Disabled,
    WarmingUp,
    Ready,
    CatchingUp,
    Stale,
    Degraded,
    Faulted
}
```

```csharp
public sealed class MarketAnalysisActor
{
    public Task RunAsync(
        ChannelReader<LiveMarketEvent> input,
        ChannelWriter<MarketAnalysisUpdate> output,
        CancellationToken cancellationToken);
}
```

Owns:

- M1 chronological sequence;
- `MultiTimeframeAggregator`;
- `ChartAnnotationEngine`;
- latest immutable snapshots;
- market-data quality;
- latest quote;
- current regime;
- cross-market readiness marker;
- duplicate candle detection.

Different market actors may run concurrently.

One actor must process its own events sequentially.

## 12.1 Output

```csharp
public sealed record MarketAnalysisUpdate
{
    public required InstrumentKey Instrument { get; init; }

    public required DateTimeOffset AvailableAt { get; init; }

    public required IReadOnlySet<BarInterval> ClosedIntervals { get; init; }

    public required MultiTimeframeAnalysis Analysis { get; init; }

    public required MarketDataHealthSnapshot Health { get; init; }

    public required long MarketSequence { get; init; }
}
```

---

# 13. Warm-up

## 13.1 Requirements

Warm-up must satisfy component readiness, not only a fixed number of days.

Check:

- ATR;
- RSI;
- Bollinger;
- ADX;
- Efficiency Ratio;
- Donchian;
- swings;
- market structure;
- regime calibration;
- required multi-timeframes;
- setup calibration compatibility;
- currency-strength basket;
- correlation lookback.

## 13.2 Readiness report

```csharp
public sealed record LiveAnalysisReadiness
{
    public required InstrumentKey Instrument { get; init; }

    public required bool Ready { get; init; }

    public required IReadOnlyList<ComponentReadiness> Components { get; init; }
}
```

```csharp
public sealed record ComponentReadiness
{
    public required string Component { get; init; }

    public required BarInterval Interval { get; init; }

    public required int RequiredSamples { get; init; }

    public required int AvailableSamples { get; init; }

    public required string Status { get; init; }
}
```

No executable Agent starts until all hard-required components are ready.

---

# 14. Cross-market analysis

Create one service that consumes completed closes from all market actors.

```csharp
public interface ICrossMarketAnalysisService
{
    void Apply(MarketAnalysisUpdate update);

    CrossMarketSnapshot GetSnapshot(
        DateTimeOffset availableAt);
}
```

Owns:

- FX conversion rates;
- rolling correlations;
- correlation clusters;
- currency strength;
- coverage diagnostics.

## 14.1 No lookahead

A cross-market snapshot at time `T` uses only updates available at or before `T`.

## 14.2 Currency strength

Use leave-one-instrument-out calculation for the candidate pair where possible.

Insufficient basket coverage is neutral.

## 14.3 Correlation

Use the configured completed correlation interval.

Never use current unfinished returns.

---

# 15. Agent supervisor

Create one Agent runtime per:

```text
instrument × strategy assignment
```

```csharp
public sealed record AgentInstanceKey(
    InstrumentKey Instrument,
    string StrategyId);
```

```csharp
public sealed class AgentSupervisor
{
    public Task ApplyAsync(
        MarketAnalysisUpdate update,
        CrossMarketSnapshot crossMarket,
        CancellationToken cancellationToken);
}
```

Each Agent runtime owns:

- setup/scoping state;
- last evaluated snapshot version;
- strategy high-watermark;
- shadow outcome state;
- current position ownership references;
- diagnostics.

It does not own account balance.

## 15.1 Triggering

Evaluate only when the Agent's trigger interval closes and all required snapshots are available.

## 15.2 Shadow Agents

Shadow Agents execute the entire decision pipeline but stop before portfolio reservation.

Record:

- raw decision;
- setup calibration result;
- meta-model result;
- suggested risk;
- suggested quantity;
- reason for non-execution;
- future outcome where measurable.

---

# 16. Decision pipeline

The shared decision pipeline must produce a fully audited candidate.

```text
Agent structural logic
    ↓
Feature policy
    ↓
Regime entry profile
    ↓
Raw confidence
    ↓
SetupCalibrationPolicy
    ↓
MetaLabelFeatureFactory
    ↓
CalibratedSetupMetaModel
    ↓
TradingConditionFilter
    ↓
Candidate
```

## 16.1 Candidate model

```csharp
public sealed record LiveTradeCandidate
{
    public required string CandidateId { get; init; }

    public required string DecisionId { get; init; }

    public required string SetupId { get; init; }

    public required string StrategyId { get; init; }

    public required InstrumentKey Instrument { get; init; }

    public required AgentAction Action { get; init; }

    public required DateTimeOffset DecisionTime { get; init; }

    public required long DecisionEpoch { get; init; }

    public required decimal ReferencePrice { get; init; }

    public required decimal StopLossPrice { get; init; }

    public decimal? TakeProfitPrice { get; init; }

    public required decimal RawConfidence { get; init; }

    public required decimal MultiTimeframeAlignment { get; init; }

    public required MarketRegime EntryRegime { get; init; }

    public required SetupCalibrationAudit SetupCalibration { get; init; }

    public required MetaLabelAudit MetaLabel { get; init; }

    public required TradingConditionDecision TradingCondition { get; init; }

    public required LiveAgentMode AgentMode { get; init; }
}
```

## 16.2 Meta-label audit

```csharp
public sealed record MetaLabelAudit
{
    public required bool Trade { get; init; }

    public required decimal Probability { get; init; }

    public required decimal RiskMultiplier { get; init; }

    public required string ReasonCode { get; init; }

    public required string ModelVersion { get; init; }

    public string? BucketId { get; init; }

    public int? Samples { get; init; }

    public decimal? ExpectedR { get; init; }

    public decimal? BrierScore { get; init; }
}
```

The multiplier must be clamped to `0..1` and validated.

---

# 17. Decision epoch coordinator

Network response order must not determine which market receives capital first.

Create:

```csharp
public sealed class LiveDecisionEpochCoordinator
{
    public Task SubmitCandidateAsync(
        LiveTradeCandidate candidate,
        CancellationToken cancellationToken);
}
```

## 17.1 Epoch definition

Group candidates by canonical completed entry-candle close time.

For example:

```text
2026-07-20 10:15:00Z
```

## 17.2 Barrier

The coordinator waits until:

- every expected market actor for that epoch reports complete; or
- a short timeout expires.

Missing/unhealthy markets are marked unavailable and cannot trade in that epoch.

## 17.3 Ordering

Within an epoch, order candidates deterministically:

1. portfolio score descending;
2. requested risk ascending;
3. instrument ordinal;
4. strategy ID ordinal;
5. decision ID ordinal.

## 17.4 No retroactive order

Approved orders become eligible only after the decision epoch closes and the portfolio decision is committed.

---

# 18. Risk-budget composition

The risk budget must be fully explainable.

```text
final risk budget =
    base risk
  × setup calibration multiplier
  × meta-label multiplier
  × regime multiplier
  × trading-condition multiplier
  × drawdown multiplier
  × volatility multiplier
  × liquidity multiplier
  × correlation multiplier
  × strategy allocation multiplier
```

Every multiplier:

- defaults to one when neutral;
- may reduce risk;
- may reject with zero;
- may not increase above one in the first production release.

```csharp
public sealed record LiveRiskBudgetAudit
{
    public required decimal BaseRiskAmount { get; init; }

    public required decimal SetupMultiplier { get; init; }

    public required decimal MetaLabelMultiplier { get; init; }

    public required decimal RegimeMultiplier { get; init; }

    public required decimal TradingConditionMultiplier { get; init; }

    public required decimal DrawdownMultiplier { get; init; }

    public required decimal VolatilityMultiplier { get; init; }

    public required decimal LiquidityMultiplier { get; init; }

    public required decimal CorrelationMultiplier { get; init; }

    public required decimal StrategyMultiplier { get; init; }

    public required decimal CombinedMultiplier { get; init; }

    public required decimal FinalRiskAmount { get; init; }
}
```

---

# 19. Position sizing

Reuse `PositionSizer`.

Required live inputs:

- authoritative account equity;
- entry executable price;
- original stop;
- account currency;
- quote-to-account conversion;
- expected spread;
- expected slippage;
- commission;
- OANDA minimum units;
- maximum units;
- quantity precision;
- margin rate;
- portfolio remaining budget.

## 19.1 Hard cap

`PreTradeRiskManager` must independently enforce a maximum per-trade risk even when fixed quantity mode is used.

Position sizing and risk validation are separate safeguards.

## 19.2 Quantity result

```csharp
public sealed record LivePositionSizingAudit
{
    public required decimal RequestedRisk { get; init; }

    public required decimal RawQuantity { get; init; }

    public required decimal BrokerNormalizedQuantity { get; init; }

    public required decimal EstimatedStopLoss { get; init; }

    public required decimal EstimatedMargin { get; init; }

    public required decimal ExpectedCosts { get; init; }

    public required string QuoteConversionPath { get; init; }

    public required string ReasonCode { get; init; }
}
```

Missing conversion fails closed.

---

# 20. Portfolio admission

Create one serialized `LiveOpportunityCoordinator`.

```csharp
public interface ILiveOpportunityCoordinator
{
    Task<IReadOnlyList<PortfolioDecision>> EvaluateEpochAsync(
        IReadOnlyList<LiveTradeCandidate> candidates,
        LivePortfolioSnapshot portfolio,
        CancellationToken cancellationToken);
}
```

Reuse:

- `PortfolioRiskManager`;
- `CapitalAllocator`;
- `PortfolioReservationBook`;
- `CurrencyExposureCalculator`;
- `RollingCorrelationClusters`.

## 20.1 Limits

Enforce:

- per-trade stop risk;
- total portfolio heat;
- pending risk;
- strategy heat;
- instrument heat;
- currency gross exposure;
- currency net exposure;
- correlation-cluster heat;
- maximum positions;
- account margin usage;
- single-position margin;
- unallocated reserve.

## 20.2 Reservation

Risk and margin are reserved before broker submission.

Reservation states:

```csharp
public enum LiveReservationState
{
    Created,
    BrokerSubmissionPending,
    BrokerAccepted,
    PartiallyFilled,
    Filled,
    Released,
    Rejected,
    Expired
}
```

## 20.3 Unknown submission

If broker certainty is unknown:

- keep reservation;
- pause new entries if required;
- reconcile by client order ID;
- do not retry blindly;
- release only after authoritative not-found/rejected proof.

---

# 21. Manual approval

Manual mode must use the exact same decision and risk pipeline as autonomous mode.

The only difference is the final approval gate.

```csharp
public sealed record ManualApprovalRequest
{
    public required string CandidateId { get; init; }

    public required string CandidateFingerprint { get; init; }

    public required string ApprovedBy { get; init; }

    public required DateTimeOffset ApprovedAt { get; init; }
}
```

Candidate expiry:

- if quote moves materially;
- if stop/target becomes invalid;
- if portfolio state changes;
- if approval timeout passes;
- if new entry candle invalidates setup.

Approval must revalidate account, quote and portfolio before submission.

---

# 22. Live execution gateway

Wrap the existing `ExecutionCoordinator`.

```csharp
public interface ILiveExecutionGateway
{
    Task<LiveExecutionResult> SubmitEntryAsync(
        PortfolioApprovedDecision decision,
        CancellationToken cancellationToken);

    Task<LiveExecutionResult> SubmitCloseAsync(
        PositionCloseCommand command,
        CancellationToken cancellationToken);

    Task<LiveExecutionResult> SubmitReductionAsync(
        PositionReductionCommand command,
        CancellationToken cancellationToken);

    Task<LiveExecutionResult> AmendProtectiveStopAsync(
        ProtectiveStopAmendmentCommand command,
        CancellationToken cancellationToken);

    Task CancelOrderAsync(
        string brokerOrderId,
        CancellationToken cancellationToken);
}
```

## 22.1 Serialized writer

Only one component writes broker commands.

Use a bounded command channel.

## 22.2 Client order ID

Create a deterministic ID from:

```text
broker account
policy revision
strategy ID
instrument
decision ID
action
```

OANDA already supports `clientExtensions.id`.

Keep within OANDA length restrictions.

## 22.3 Idempotency

Before resubmitting:

1. search local registry;
2. search recent broker events;
3. fetch open orders;
4. inspect transactions where available;
5. match client order ID.

No automatic retry while execution certainty is `Unknown`.

## 22.4 Atomic initial protection

Entry request must include:

```text
StopLossOnFill
optional TakeProfitOnFill
```

If stop mapping is unavailable or rejected, reject the entry.

---

# 23. OANDA-specific capability matrix

Create:

```csharp
public sealed record LiveBrokerCapabilityMatrix
{
    public bool MultiInstrumentPricingStream { get; init; }

    public bool TransactionStream { get; init; }

    public bool ClientOrderIds { get; init; }

    public bool AtomicStopOnFill { get; init; }

    public bool AtomicTakeProfitOnFill { get; init; }

    public bool SafeProtectiveStopReplacement { get; init; }

    public bool PartialClose { get; init; }

    public bool ReduceOnlyClose { get; init; }

    public bool QueryByClientOrderId { get; init; }
}
```

The live plan validator rejects any enabled feature unsupported by the broker matrix.

---

# 24. OANDA stop replacement

Dynamic trailing cannot be considered active until this is implemented and certified.

Create OANDA commands for safe dependent-order replacement.

Requirements:

- preserve the existing stop until replacement succeeds;
- use OANDA-supported trade dependent-order endpoint;
- include client extension/correlation ID where supported;
- return explicit `Accepted`, `Replaced`, `Rejected`, `Unsupported`, `Unknown`;
- confirm through transaction stream;
- reconcile through REST;
- never widen the stop;
- never change the wrong trade.

Do not implement:

```text
cancel old stop
then create new stop
```

without an atomic or safely ordered broker-supported replacement.

Until certification, live policy must use:

```text
fixed broker stop
+ strategy market exits
```

and the Dashboard must clearly display that trailing amendments are disabled.

---

# 25. OANDA partial close

Add an explicit broker abstraction rather than simulating a close through a generic unmanaged opposite order.

```csharp
public interface IPositionReductionBrokerClient
{
    Task<PositionReductionResult> ReducePositionAsync(
        ReduceBrokerPositionRequest request,
        CancellationToken cancellationToken);
}
```

The OANDA adapter should use the correct trade-close or position-close endpoint.

Requirements:

- reduce-only;
- quantity cannot exceed remaining owned quantity;
- strategy ownership is verified;
- resulting broker position is reconciled;
- dependent stop/target quantities are reconciled;
- partial close event updates realised P/L and reservation heat.

---

# 26. Broker event processor

Use the transaction stream as the immediate event source.

```csharp
public interface ILiveBrokerEventProcessor
{
    Task RunAsync(CancellationToken cancellationToken);
}
```

Map OANDA transactions into normalized events:

```text
OrderAccepted
OrderRejected
OrderFilled
OrderPartiallyFilled
OrderCancelled
StopCreated
StopReplaced
StopTriggered
TakeProfitTriggered
TradeReduced
TradeClosed
FinancingPosted
MarginCallEntered
MarginCallExited
Heartbeat
```

Every normalized event must contain:

- broker transaction ID;
- broker order/trade ID;
- client order ID;
- instrument;
- quantity;
- price;
- timestamp;
- reason;
- related IDs.

Persist before publishing downstream where practical.

---

# 27. Order and position registry

Create the authoritative local representation.

```csharp
public enum LiveOrderState
{
    Candidate,
    PortfolioApproved,
    SubmissionPending,
    SubmissionUnknown,
    Accepted,
    PartiallyFilled,
    Filled,
    CancelPending,
    Cancelled,
    Rejected,
    Expired,
    Closed
}
```

```csharp
public interface ILiveOrderPositionRegistry
{
    LivePortfolioSnapshot Snapshot { get; }

    RegistryApplyResult Apply(
        NormalizedBrokerEvent brokerEvent);

    RegistryApplyResult ApplyReconciliation(
        BrokerReconciliationSnapshot snapshot);
}
```

Track:

```text
StrategyId
Instrument
SetupId
DecisionId
CandidateId
ClientOrderId
BrokerOrderId
BrokerTradeId
ReservationId
RiskClusterId
PolicyRevision
Calibration IDs
Meta-model version
```

Events must be idempotent by broker transaction ID.

---

# 28. Reconciliation

Use both:

```text
transaction stream
periodic REST snapshots
```

```csharp
public interface ILiveBrokerReconciler
{
    Task<BrokerReconciliationReport> ReconcileAsync(
        ReconciliationTrigger trigger,
        CancellationToken cancellationToken);
}
```

## 28.1 Triggers

- startup;
- periodic timer;
- pricing reconnect;
- transaction reconnect;
- unknown submission;
- stop amendment;
- partial close;
- cancellation failure;
- manual request;
- before resuming from degraded state.

## 28.2 Differences

Detect:

- broker order missing locally;
- local order missing at broker;
- position quantity mismatch;
- average price mismatch;
- protective stop missing;
- stop price mismatch;
- target mismatch;
- unknown broker trade;
- reservation without order;
- order without reservation;
- account margin mismatch.

## 28.3 Response

```text
minor explainable mismatch:
    repair local projection

ownership uncertain:
    pause new entries

missing protective stop:
    critical safety action

unowned broker position:
    alert and require explicit adoption or flatten
```

No automatic adoption without policy.

---

# 29. Live trade management

Create one `LivePositionManager` per strategy-owned position/lot.

```csharp
public sealed class LivePositionManager
{
    public Task OnQuoteAsync(...);

    public Task OnAnalysisAsync(...);

    public Task OnBrokerEventAsync(...);
}
```

## 29.1 Mechanical clock

Run on quote/execution updates:

- MFE/MAE;
- emergency account giveback;
- profit-floor breach;
- fixed-R scale-out eligibility;
- cost-aware break-even eligibility.

## 29.2 Fast structure

Entry timeframe, e.g. 5m:

- local swing trailing;
- local deterioration;
- rejection against trade;
- first structural reduction.

## 29.3 Main structure

Confirmation timeframe, e.g. 15m/30m:

- main swing/zone/channel trailing;
- momentum decay;
- stagnation;
- scale-out;
- target adjustment.

## 29.4 Thesis

Trend timeframe, e.g. 1h/2h:

- higher-timeframe invalidation;
- runner permission;
- full thesis exit.

## 29.5 Management calibration

Resolve management artifact by:

- strategy;
- instrument group;
- regime;
- setup type;
- confidence bucket.

Record the actual effective thresholds.

## 29.6 Unsupported capabilities

If stop amendment is unavailable:

- do not pretend stop moved;
- use market reduction/exit where policy permits;
- maintain the original broker stop;
- emit `ProtectiveStopAmendmentUnsupported`.

---

# 30. Safety controller

Create one account-level controller and one strategy-level controller.

```csharp
public enum LiveSafetyDirective
{
    None,
    PauseNewEntries,
    CancelPendingEntries,
    ReducePositions,
    FlattenAll
}
```

Monitor:

- stale pricing;
- stale completed candles;
- transaction stream loss;
- reconciliation mismatch;
- missing stop;
- account drawdown;
- daily loss;
- strategy drawdown;
- portfolio heat;
- currency concentration;
- correlation concentration;
- margin use;
- repeated rejects;
- unknown submissions;
- duplicate broker events;
- persistence failure;
- account ownership lease failure.

## 30.1 Hard safety rules

- no entry without stop;
- no new entries while broker state is uncertain;
- no new entries while market data is stale;
- no duplicate `DecisionId`;
- no risk above hard account limits;
- no autonomous live environment;
- no use of unverified calibration artifact;
- no stop amendment that widens risk;
- no partial close beyond owned quantity.

## 30.2 Kill switch

Required actions:

```text
Pause new entries
Cancel pending entries
Flatten all
Stop engine while preserving broker stops
```

Flatten must be serialized and reconciled.

---

# 31. Persistence

Create append-only journals plus periodic checkpoint.

```text
.cache/live-trading/{account-id}/
    active-plan.json
    engine-checkpoint.json
    market-checkpoints/
    decisions.ndjson
    portfolio-decisions.ndjson
    broker-events.ndjson
    orders.ndjson
    positions.ndjson
    management-events.ndjson
    safety-events.ndjson
    account-snapshots.ndjson
    reconciliation.ndjson
    COMPLETE-SHUTDOWN
```

## 31.1 Writer

One serialized persistence writer.

Use:

- atomic temporary file then move for snapshots;
- append with flush policy for journals;
- schema version;
- checksum;
- quarantine for corrupt snapshots;
- no swallowed integrity mismatch.

## 31.2 Restart recovery

```text
Acquire lease
    ↓
Load checkpoint and journals
    ↓
Connect OANDA
    ↓
Fetch account/orders/positions
    ↓
Replay unapplied broker events
    ↓
Reconcile
    ↓
Warm analysis
    ↓
Resume management of existing positions
    ↓
Enable entries only after explicit health gate
```

---

# 32. Monitoring models

```csharp
public sealed record LiveTradingSnapshot
{
    public required LiveEngineState State { get; init; }

    public required string InstanceId { get; init; }

    public required string AccountIdMasked { get; init; }

    public required int PolicyRevision { get; init; }

    public required BrokerConnectionHealth BrokerHealth { get; init; }

    public required IReadOnlyList<LiveMarketSnapshot> Markets { get; init; }

    public required IReadOnlyList<LiveAgentSnapshot> Agents { get; init; }

    public required LivePortfolioSnapshot Portfolio { get; init; }

    public required AccountSafetySnapshot Safety { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
```

For each Agent display:

- state;
- mode;
- trend/setup/confirmation/entry;
- regime;
- signal funnel stage;
- rejection reason;
- raw confidence;
- MTF alignment;
- setup calibration;
- meta bucket;
- meta probability;
- meta expected R;
- risk multiplier;
- last candidate;
- open owned position.

---

# 33. API

Map in `LiveTradingHost`.

```text
GET    /api/live/status
GET    /api/live/health
GET    /api/live/markets
GET    /api/live/agents
GET    /api/live/candidates
GET    /api/live/orders
GET    /api/live/positions
GET    /api/live/portfolio
GET    /api/live/events

POST   /api/live/plan/validate
POST   /api/live/start
POST   /api/live/pause
POST   /api/live/resume
POST   /api/live/stop
POST   /api/live/cancel-pending
POST   /api/live/flatten

POST   /api/live/candidates/{id}/approve
POST   /api/live/candidates/{id}/reject
POST   /api/live/positions/{id}/close
POST   /api/live/positions/{id}/reduce
```

SignalR:

```text
/hubs/live-trading
```

## 33.1 Security

Default bind:

```text
127.0.0.1 only
```

If remote binding is enabled:

- authentication required;
- TLS required;
- CSRF-safe command model;
- audit user identity;
- role-based dangerous actions;
- no OANDA token exposure.

---

# 34. Dashboard workspace

Add a new top-level workspace:

```text
Live Demo
```

Separate it visually from Simulator and Research.

## 34.1 Sections

### Engine

- state;
- policy revision;
- mode;
- start/pause/resume/stop;
- kill switch.

### Connections

- REST;
- pricing stream;
- transaction stream;
- last heartbeat;
- last reconciliation;
- market-data delay.

### Markets

- selected instruments;
- latest bid/ask;
- spread;
- M1 close;
- analysis readiness;
- regime;
- data health.

### Agents

- mode;
- setup state;
- feature switches;
- calibration IDs;
- meta decision;
- candidate status.

### Portfolio

- equity;
- margin;
- heat;
- reservations;
- strategy/instrument/currency/cluster risk;
- high-watermark.

### Orders and positions

- ownership IDs;
- stop/target;
- remaining quantity;
- management stage;
- broker reconciliation.

### Events

- reason-coded timeline;
- filtering by market/strategy/order/severity.

## 34.2 Confirmation

Dangerous operations require a modal confirmation showing:

- account;
- environment;
- affected positions;
- exact action.

---

# 35. Observability

Use structured logs.

Required identifiers:

```text
InstanceId
AccountIdMasked
PolicyRevision
Instrument
StrategyId
CandidateId
DecisionId
SetupId
ReservationId
ClientOrderId
BrokerOrderId
BrokerTradeId
ReconciliationId
```

Metrics:

- quote delay;
- candle delay;
- market actor queue depth;
- Agent evaluation time;
- candidate count;
- rejection counts by stage;
- portfolio evaluation time;
- broker command latency;
- unknown submissions;
- reconciliation mismatch count;
- stop amendment success;
- persistence queue depth.

No secrets in logs.

---

# 36. Concurrency model

Recommended concurrency for ten markets and two strategies:

```text
1 pricing stream reader
1 transaction stream reader
1 M1 completion scheduler
10 market actors
20 Agent runtimes
1 decision epoch coordinator
1 portfolio actor
1 broker command writer
1 broker event applier
1 reconciliation worker
1 persistence writer
```

Serialize:

- account state changes;
- portfolio reservations;
- opportunity ranking;
- broker writes;
- broker event application;
- registry mutation.

Parallelize:

- independent market analysis;
- read-only Agent evaluation;
- Dashboard serialization;
- offline shadow outcome analysis.

---

# 37. Fault boundaries

## 37.1 Market failure

Effect:

- mark one market degraded;
- stop entries for that market;
- continue other markets;
- continue managing its existing broker position using quotes if healthy;
- reconcile before re-enabling.

## 37.2 Agent failure

Effect:

- disable one Agent instance;
- keep market analysis running;
- manage existing position through last known management policy or safety-only mode;
- alert.

## 37.3 Portfolio failure

Effect:

- pause all new entries;
- do not affect broker protective stops;
- continue event processing;
- require recovery/restart.

## 37.4 Broker stream failure

Pricing stream:

- pause new entries;
- use REST reconciliation;
- reconnect and catch up.

Transaction stream:

- pause new entries;
- poll REST;
- reconcile;
- resume only after stream recovery.

## 37.5 Persistence failure

- pause new entries;
- continue broker event processing in memory for a bounded emergency period;
- alert;
- flatten only according to explicit safety policy.

---

# 38. Implementation phases

## Phase 0 — baseline and contracts

Deliver:

- clean build/test report;
- project dependency map;
- broker capability matrix;
- extracted shared calibration project;
- shared strategy pipeline factory;
- policy bundle and validation.

Acceptance:

- simulator behaviour unchanged;
- meta-model remains wired;
- artifacts resolve from shared repository;
- no live execution.

## Phase 1 — observe-only live data

Deliver:

- `LiveTrading`;
- `LiveTrading.Oanda`;
- `LiveTradingHost`;
- account lease;
- one OANDA pricing stream;
- completed M1 coordinator;
- market actors;
- Dashboard live status;
- no orders.

Acceptance:

- five markets run for 72 hours;
- no duplicate M1 candle;
- reconnect catches up;
- no strategic unfinished candle;
- memory bounded;
- browser disconnect does not stop host.

## Phase 2 — production and shadow Agents

Deliver:

- Agent supervisor;
- shared pipeline factory use;
- policy and artifacts;
- shadow Agents;
- signal funnel;
- decision epoch coordinator.

Acceptance:

- Agent output matches simulator on replayed identical completed candles;
- feature-switch differences are visible;
- meta multiplier never exceeds one;
- no orders.

## Phase 3 — portfolio and manual approval

Deliver:

- risk-budget audit;
- position sizing;
- portfolio coordinator;
- reservation book;
- manual candidate approval;
- OANDA protected entry;
- registry;
- transaction processing;
- reconciliation.

Acceptance:

- one approved demo trade completes end to end;
- stop exists at broker;
- duplicate approval cannot duplicate order;
- restart recovers state;
- unknown submission does not retry blindly.

## Phase 4 — autonomous demo entry

Deliver:

- autonomous mode;
- hard demo guard;
- safety controller;
- stale-data pause;
- account/margin/heat limits;
- kill switch.

Initial limits:

```text
risk per trade: 0.05%–0.10%
portfolio heat: 0.25%–0.50%
maximum positions: 2
maximum margin usage: 10%–15%
pyramiding: disabled
Improved Agent executable
Legacy shadow-only
```

Acceptance:

- minimum two weeks stable observe/manual run;
- no unknown unresolved orders;
- no reconciliation mismatch;
- kill switch certified.

## Phase 5 — partial close and fixed-stop management

Deliver:

- OANDA explicit partial close;
- strategy market exits;
- scale-out using market reduction;
- original broker stop retained.

Acceptance:

- quantity reconciliation;
- realised P/L attribution;
- stop quantity remains valid;
- no reversal.

## Phase 6 — dynamic stop management

Deliver:

- safe OANDA stop replacement;
- break-even;
- profit floors;
- MFE giveback;
- structural trailing;
- management calibration.

Acceptance:

- every amendment confirmed by transaction and REST;
- old protection never disappears unsafely;
- rejected amendment leaves old stop;
- restart recovers current stop.

## Phase 7 — broader multi-agent rollout

Deliver:

- more instruments;
- Legacy optional manual/autonomous after validation;
- currency-strength evidence;
- correlation clusters;
- broader risk allocation;
- live shadow ablation reports.

Acceptance:

- portfolio limits remain authoritative;
- simultaneous signals ranked deterministically;
- no capital race.

---

# 39. Testing strategy

## 39.1 Unit tests

- policy validation;
- artifact compatibility;
- engine state transitions;
- candidate expiry;
- decision epoch ordering;
- risk multiplier composition;
- client order ID generation;
- registry idempotency;
- reservation lifecycle;
- safety directives;
- stop improvement invariant.

## 39.2 Fake broker integration

Create `DeterministicLiveBroker`.

Support scripted:

- accepted entry;
- reject;
- timeout/unknown;
- partial fill;
- duplicate event;
- event reordering;
- stream disconnect;
- missing stop;
- stop replacement reject;
- partial close;
- margin call.

## 39.3 Restart tests

- entry accepted before crash;
- fill event after restart;
- unknown submission;
- open position with local checkpoint missing;
- local position absent at broker;
- corrupt checkpoint;
- duplicate transaction replay.

## 39.4 Market tests

- missed M1;
- duplicate M1;
- weekend;
- stale quote;
- delayed REST close;
- out-of-order stream quote;
- reconnect catch-up;
- simultaneous market epoch.

## 39.5 OANDA demo integration tests

Mark:

```text
[Category("Integration")]
[Explicit]
[NonParallelizable]
```

Test:

- account;
- instruments;
- pricing stream;
- transaction stream;
- tiny bracket order;
- cancel pending order;
- partial close;
- stop replacement;
- reconciliation.

Use a dedicated demo test instrument and tiny quantity.

---

# 40. Validation gates

No phase advances without:

```bash
dotnet restore TradingHub.slnx
dotnet build TradingHub.slnx -c Release
dotnet test TradingHub.slnx -c Release
```

```bash
cd Dashboard
npm ci
npm run typecheck
npm run build
npm run data:validate
```

Also run:

- 24/72-hour soak;
- reconnect test;
- host restart test;
- kill-switch test;
- unknown-submission test;
- duplicate-event test;
- account lease conflict test.

---

# 41. Suggestions

## 41.1 Start with Improved only

The Improved Agent produced too few trades for strong statistical confidence, but Legacy's recent monthly output was negative.

Recommended first deployment:

```text
Improved:
    observe/manual/autonomous progression

Legacy:
    shadow-only
```

## 41.2 Use shadow variants for filter diagnosis

Run:

```text
Improved production
Improved without secondary trend
Improved with soft price action
Improved without regime routing
Improved without meta-model
```

Only production can execute.

This gives live evidence for over-filtering without risking duplicate exposure.

## 41.3 Do not enable all markets immediately

Start with liquid major FX pairs.

Avoid beginning with:

- exotic FX;
- illiquid CFDs;
- very wide-spread markets;
- markets with uncertain conversion/margin metadata.

## 41.4 Keep live risk much lower than research risk

The first purpose is engineering validation, not profit maximization.

## 41.5 Treat broker stop replacement as a certification milestone

Do not enable dynamic trailing from configuration until OANDA integration tests prove it.

## 41.6 Keep the host independent of the Dashboard

Closing the browser must have no effect on trading.

## 41.7 Promote artifacts explicitly

Do not automatically use “latest calibration.”

Add artifact status:

```csharp
public enum CalibrationArtifactStatus
{
    Research,
    Reviewed,
    ApprovedForDemo,
    Retired
}
```

The live host accepts only `ApprovedForDemo`.

---

# 42. Non-goals for first live release

Postpone:

- real-money account support;
- neural-network ML;
- automatic retraining;
- dynamic universe discovery;
- tick-based strategic indicators;
- full order-book analytics;
- perfect historical/live execution equivalence;
- high-frequency trading;
- cross-broker smart order routing;
- automatic artifact promotion.

---

# 43. Final acceptance criteria

The first live demo platform is complete only when:

1. one host exclusively owns the OANDA demo account;
2. one pricing stream monitors a list of markets;
3. each market has isolated chronological analysis state;
4. multiple Agents consume shared immutable analysis;
5. production and shadow modes are separate;
6. research feature policy and artifact IDs are recorded;
7. setup calibration reaches the live Agent;
8. meta-label decisions reach risk sizing;
9. meta risk never exceeds one;
10. all new entries compete through one portfolio coordinator;
11. account risk and margin are authoritative;
12. every entry has a broker-side stop;
13. client order IDs are deterministic;
14. unknown submissions are reconciled, not blindly retried;
15. transaction events are persisted and idempotent;
16. periodic REST reconciliation works;
17. restart resumes management of broker positions;
18. stale data pauses entries;
19. kill switch works;
20. Dashboard disconnect does not stop the engine;
21. unsupported dynamic stop management is shown honestly;
22. OANDA demo integration tests pass;
23. full solution and Dashboard builds pass;
24. a 72-hour soak test finishes without unresolved mismatch.

---

# 44. Required deliverables from the implementing agent

The implementing AI must return:

1. exact files created and modified;
2. project-reference graph;
3. architecture diagram;
4. engine state diagram;
5. order state diagram;
6. startup/recovery flow;
7. OANDA capability matrix;
8. policy/artifact validation design;
9. concurrency design;
10. persistence schema;
11. API list;
12. Dashboard screenshots or descriptions;
13. unit/integration test totals;
14. OANDA demo test report;
15. soak-test report;
16. known limitations;
17. deferred features;
18. clean source ZIP;
19. SHA-256 checksum;
20. explicit statement of any test not run.

---

# 45. Target end-to-end flow

```text
QuantResearchRunner
    → reviewed setup calibration
    → reviewed management calibration
    → reviewed statistical meta-model
    → approved policy bundle

OANDA pricing stream
    → latest executable quotes

OANDA completed M1 REST coordinator
    → chronological M1 candles
    → per-market multi-timeframe aggregation
    → ChartAnnotator snapshots

Market actor
    → Agent production/shadow evaluation
    → setup calibration
    → statistical meta-label
    → trading conditions
    → candidate

Decision epoch coordinator
    → adaptive risk budget
    → position sizing
    → portfolio ranking
    → risk/margin reservation

Manual or autonomous approval
    → ExecutionManager
    → OANDA order with broker-side stop

OANDA transaction stream
    → normalized broker event
    → persistent registry
    → account/position reconciliation

LiveTradeManager
    → partial reduction
    → market exit
    → certified stop replacement

Safety controller
    → pause
    → cancel
    → reduce
    → flatten

Dashboard
    → observe and control
    → never owns the engine lifecycle
```

This is the authoritative implementation target.
