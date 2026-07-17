# TradingHub Quantitative Enhancements — Final Audit and Corrective Implementation Prompt

**Target repository:** the supplied `TradingHub_source.zip`  
**Target solution:** `TradingHub/TradingHub.slnx`  
**Audience:** senior .NET quantitative developer or coding AI  
**Goal:** complete and correct the quantitative-enhancement implementation without duplicating systems that already work.

---

# 1. Mission

The repository contains a substantial implementation of the earlier quantitative-enhancement specification. Many required classes, tests, Dashboard controls and simulator hooks now exist.

However, the audit found an important difference between:

```text
A class exists
```

and:

```text
The feature is connected to the real chronological simulation path,
receives valid data, affects decisions correctly, is configurable,
is observable, and is validated end to end.
```

This corrective pass must:

1. preserve completed features;
2. identify and remove misleading or disconnected configuration;
3. connect existing quantitative components to the actual simulator;
4. correct portfolio-account semantics;
5. make research/calibration features operational;
6. improve execution/data honesty;
7. add missing end-to-end tests;
8. produce a reproducible validation report.

Do not create a second simulator, duplicate manager, or parallel implementation.

---

# 2. Audit validation already performed

The following were independently run against the supplied source:

```bash
cd Dashboard
npm ci
npm run typecheck
npm run build
npm run data:validate
```

Results:

```text
Vue/TypeScript typecheck: passed
Vite production build: passed
Replay fixture validation: passed
Replay frames validated: 680
```

The audit environment could not download or locate the .NET 10 SDK because external DNS was unavailable.

Therefore, this audit does **not** claim:

```text
dotnet restore passed
dotnet build passed
dotnet test passed
```

The implementing agent must run the complete .NET build and test suite on a machine with .NET 10 before claiming completion.

---

# 3. High-level implementation status

## 3.1 Implemented and substantially connected

The following systems appear to be present and connected to production simulation paths:

- incremental Efficiency Ratio;
- Donchian channels;
- ADX/DMI;
- price-action analysis;
- anchored value-reference calculation;
- market-regime classifier and hysteresis;
- regime routing;
- trading-condition filter;
- fixed/cash/fractional risk-based sizing;
- adaptive risk multipliers;
- account and strategy equity high-watermark models;
- execution-frame mechanical trade protection;
- multi-speed structural management;
- execution modelling abstraction;
- financing abstraction;
- shared-account admission layer;
- reservation book;
- portfolio heat models;
- capital allocator;
- QuantResearch helper classes;
- confidence calibration artefact models;
- management calibration artefact models;
- meta-label contracts;
- Dashboard controls for several new features;
- replay and performance contracts for several quantitative metrics.

Do not rewrite these systems merely because their implementation is incomplete.

## 3.2 Partially implemented or disconnected

The following require corrective integration:

- rolling correlation;
- correlation-cluster risk;
- relative currency strength;
- full currency-exposure calculation;
- true shared portfolio account;
- multi-instrument shared simulation;
- economic-event provider;
- calibrated setup artefact loading;
- management-calibration artefact loading;
- meta-label model loading;
- QuantResearch execution runner;
- feature-switch mapping;
- parameter-sensitivity runner integration;
- market-regime spread/data-quality inputs;
- DST-aware trading sessions;
- anchored value-reference strategy/chart use;
- complete Dashboard/CLI exposure;
- historical bid/ask and live broker certification.

---

# 4. Critical finding: correlation configuration is currently misleading

## 4.1 What exists

The solution contains:

```text
PortfolioManager/Correlation/RollingCorrelationClusters.cs
```

and Dashboard/API fields for:

- lookback;
- minimum samples;
- soft threshold;
- hard threshold.

## 4.2 What is wrong

The production shared runtime currently constructs opportunities with:

```csharp
CorrelationPenalty = 0m
```

and resolves clusters using:

```csharp
$"cluster:{instrument.Value}"
```

This means every instrument effectively forms its own cluster.

`RollingCorrelationClusters` is not instantiated or updated by the real simulator.

Consequences:

- configured correlation settings do not affect order sizing;
- highly correlated instruments are not grouped;
- `CorrelationMultiplier` remains effectively `1`;
- replay may show cluster identifiers that imply analysis occurred when it did not;
- Dashboard correlation settings create false confidence.

## 4.3 Required implementation

Add one `RollingCorrelationClusters` instance to shared portfolio runtime.

It must receive completed return observations for **all instruments in the shared portfolio universe** at one configured interval, initially `1h`.

Required flow:

```text
completed 1h candles for all portfolio instruments
    ↓
calculate trailing log returns
    ↓
RollingCorrelationClusters.Update(...)
    ↓
correlation snapshot available at frame N
    ↓
evaluate candidate against current open/reserved positions
    ↓
apply risk multiplier and cluster ID
    ↓
rank and reserve opportunity
```

The snapshot must contain:

```csharp
public sealed record CorrelationSnapshot
{
    public required DateTimeOffset AvailableAt { get; init; }
    public required long Version { get; init; }
    public required IReadOnlyDictionary<string, decimal> PairCorrelations { get; init; }
    public required IReadOnlyDictionary<InstrumentKey, string> ClusterByInstrument { get; init; }
    public required IReadOnlyDictionary<InstrumentKey, int> SampleCounts { get; init; }
}
```

Do not use current/future returns.

### Required admission wiring

Replace:

```csharp
CorrelationPenalty = 0m
CorrelationClusterFactory = opportunity =>
    $"cluster:{opportunity.Decision.Instrument.Value}"
```

with real evaluation:

```csharp
CorrelationPenaltyDecision correlation =
    correlationClusters.Evaluate(
        candidateInstrument,
        candidateDirection,
        openLots,
        activeReservations,
        snapshot);
```

Set:

```text
PortfolioOpportunity.CorrelationPenalty
AgentDecision.CorrelationRiskMultiplier
AgentDecision.RiskClusterId
PortfolioReservation.CorrelationClusterId
```

from this result.

### Required tests

- correlated same-direction candidate receives soft penalty;
- hard-correlated candidate shares cluster;
- inverse/hedging direction receives only capped hedge credit;
- insufficient samples use conservative fallback;
- no future returns are used;
- sequential and parallel runs produce the same clusters and allocations;
- Dashboard correlation fields materially change a deterministic test result.

---

# 5. Critical finding: currency-strength calculation is not used

## 5.1 What exists

The solution contains:

```text
PortfolioManager/CurrencyStrength/CurrencyStrengthCalculator.cs
```

and `MetaLabelFeatures` has:

```csharp
CurrencyStrengthDifferential
```

## 5.2 What is missing

No production market-data coordinator builds a currency basket.

No real simulation path creates `CurrencyPairReturn` observations.

No Agent receives a populated currency-strength snapshot.

No capital allocation or strategy-confidence logic uses the calculator.

The differential is therefore null or externally injected only.

## 5.3 Required implementation

Add a cross-market analysis coordinator.

Suggested project location:

```text
PortfolioManager/CrossMarket/
    CrossMarketAnalysisCoordinator.cs
    CrossMarketSnapshot.cs
```

Required configuration:

```csharp
public sealed record CurrencyStrengthOptions
{
    public bool Enabled { get; init; }
    public BarInterval Interval { get; init; } = BarInterval.Hours(1);
    public int ReturnLookbackBars { get; init; } = 12;
    public int VolatilityLookbackBars { get; init; } = 120;
    public decimal MinimumCurrencyCoveragePercent { get; init; } = 60m;
    public IReadOnlyDictionary<string, IReadOnlyList<InstrumentKey>> Baskets { get; init; }
        = new Dictionary<string, IReadOnlyList<InstrumentKey>>();
}
```

The shared simulator must obtain historical candles for the configured basket instruments.

Do not reuse the traded pair inside its own strength differential when enough alternative pairs exist. Use leave-one-instrument-out computation.

Add to multi-timeframe market context:

```csharp
public CurrencyStrengthSnapshot? CurrencyStrength { get; init; }
```

For `GBP/JPY`:

```text
differential = GBP score - JPY score
```

Use this initially as:

- soft confirmation;
- risk multiplier;
- meta-label feature;
- opportunity-ranking feature.

Do not make it a hard veto until validated out of sample.

### Required tests

- leave-one-pair-out basket;
- base/quote direction inversion;
- insufficient coverage;
- missing instrument data;
- no-lookahead availability;
- stronger base/weaker quote supports long candidate;
- disagreement reduces but does not automatically reject;
- deterministic results with unordered input data.

---

# 6. Critical finding: shared portfolio mode is not a true shared broker account

## 6.1 Current architecture

`SharedPortfolioRuntime` coordinates several strategy sessions.

However, each session still owns:

```text
its own SimulatedBrokerClient
its own account state
its own order collection
its own position collection
its own starting balance
```

The shared runtime then constructs an aggregate account by summing deltas from these separate accounts.

This is a coordinated federation of isolated accounts, not one authoritative broker account.

## 6.2 Why this needs attention

Potential problems:

- each local session can reason against a full account balance before shared admission;
- shared margin is reconstructed rather than authoritative;
- same-instrument strategy positions cannot accurately model broker netting;
- one broker-side position cannot be reconciled to multiple strategy lots;
- strategy-local order state can diverge from a real shared account;
- account safety is based on aggregated snapshots rather than one ledger;
- live broker mapping cannot reuse this model directly;
- cross-instrument portfolio behaviour cannot be tested because a simulation request contains one instrument.

## 6.3 Required architecture

Implement one authoritative shared broker runtime:

```csharp
public sealed class SharedPortfolioRuntime
{
    public required SimulatedBrokerClient Broker { get; init; }
    public required SharedPortfolioLedger Ledger { get; init; }
    public required VirtualLotLedger VirtualLots { get; init; }
    public required IPortfolioRiskManager RiskManager { get; init; }
    public required ICapitalAllocator CapitalAllocator { get; init; }
    public required IPortfolioReservationBook Reservations { get; init; }
    public required ITradingSafetyController AccountSafety { get; init; }
}
```

Strategies must share:

- account;
- balance;
- realised P/L;
- unrealised P/L;
- margin;
- pending orders;
- broker positions;
- financing;
- equity high-watermark.

Strategies must retain:

- setup state;
- decision state;
- strategy attribution;
- virtual lots;
- strategy high-watermark;
- management state for their lots.

## 6.4 Virtual lot ledger

Add:

```csharp
public sealed record StrategyPositionLot
{
    public required string LotId { get; init; }
    public required string StrategyId { get; init; }
    public required string SetupId { get; init; }
    public required string DecisionId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required OrderSide Side { get; init; }
    public required decimal InitialQuantity { get; init; }
    public required decimal RemainingQuantity { get; init; }
    public required decimal EntryPrice { get; init; }
    public required decimal InitialStop { get; init; }
    public decimal? CurrentStop { get; init; }
    public decimal? Target { get; init; }
    public required decimal RealizedProfitLoss { get; init; }
    public required decimal Financing { get; init; }
}
```

For a broker with netting:

```text
one broker position
    ↔
many strategy-owned internal lots
```

All partial exits, targets, stops and financing must be allocated to virtual lots deterministically.

## 6.5 Backward compatibility

Keep:

```text
IndependentStrategyAccounts
```

exactly as the strategy-comparison mode.

Replace current shared mode semantics with true:

```text
SharedPortfolioAccount
```

Do not silently change historical result identities. Increment manifest and simulation configuration schema.

---

# 7. Critical finding: the simulator is still fundamentally single-instrument

## 7.1 Current request model

A simulation request contains one:

```csharp
InstrumentKey Instrument
```

The shared mode therefore combines strategies only on one instrument.

It cannot correctly exercise:

- correlation;
- currency baskets;
- multi-instrument opportunity competition;
- margin reservation across markets;
- currency exposure;
- portfolio heat across instruments;
- opportunity ranking across simultaneous signals.

## 7.2 Required multi-instrument mode

Add:

```csharp
public sealed record PortfolioInstrumentRequest
{
    public required string BrokerId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required HistoricalSourceOptions Source { get; init; }
    public required IReadOnlyList<string> StrategyIds { get; init; }
    public bool TradeEnabled { get; init; } = true;
    public bool AnalysisOnly { get; init; }
}
```

Update `BacktestRequest`:

```csharp
public IReadOnlyList<PortfolioInstrumentRequest> Instruments { get; init; } = [];
```

For backward compatibility:

```text
if Instruments is empty:
    convert existing singular Instrument into one-item list
```

## 7.3 Portfolio clock

Use one chronological execution clock across instruments.

At each timestamp:

```text
read all instrument frames available at timestamp T
process existing fills per instrument
update per-instrument analysis
update cross-market analysis
collect all strategy opportunities
rank opportunities once
reserve and submit approved orders
write events
advance
```

Do not process one entire instrument year and then the next.

Use a deterministic priority queue keyed by:

```text
AvailableAt
InstrumentKey.Value
SourceSequence
```

## 7.4 Missing-data rules

Define:

- whether one instrument gap pauses the whole frame;
- whether analysis-only basket instruments may be stale;
- maximum cross-market staleness;
- no fabricated candles;
- weekend/session-gap treatment.

---

# 8. Critical finding: currency risk uses an inaccurate 50/50 stop-risk split

## 8.1 Current production behaviour

`SharedPortfolioRuntime` computes currency risk approximately as:

```text
half of stop risk assigned to base currency
half assigned to quote currency
```

This occurs both for open lots and reservations.

A proper `CurrencyExposureCalculator` exists but is not used by the shared runtime.

## 8.2 Why the approximation is wrong

Currency concentration is an exposure problem, not merely a stop-risk split.

For a long `GBP/JPY` position:

```text
+GBP notional
-JPY notional
```

The amount on each side is not represented by splitting planned stop loss in half.

This can understate or overstate concentration and make currency limits unreliable.

## 8.3 Required correction

Use the existing `CurrencyExposureCalculator` with a complete `FxConversionSnapshot`.

Shared runtime must maintain contemporaneous conversion rates for every involved currency.

Required outputs:

```text
gross exposure by currency
net exposure by currency
risk to stop by currency
missing conversions
```

Portfolio risk policy should support separate limits:

```csharp
public decimal MaximumNetCurrencyExposurePercent { get; init; }
public decimal MaximumGrossCurrencyExposurePercent { get; init; }
public decimal MaximumCurrencyStopRiskPercent { get; init; }
```

Do not call all three “currency heat”.

Reject or degrade when required conversion data is missing.

### Required tests

- long and short base/quote decomposition;
- account currency equals base;
- account currency equals quote;
- cross-currency conversion;
- multiple correlated GBP positions;
- offsetting exposure;
- reservation plus open position;
- conversion missing;
- financing and realised P/L do not corrupt exposure.

---

# 9. Critical finding: configured correlation options currently do not alter runtime

The API and Dashboard expose:

```text
CorrelationLookbackBars
CorrelationMinimumSamples
CorrelationSoftThreshold
CorrelationHardThreshold
```

but the shared runtime does not construct or use `RollingCorrelationClusters`.

The corrective implementation must include an end-to-end test proving:

```text
same candles + different correlation thresholds
    → different allocation/rejection result
```

Until this test exists, do not claim correlation protection is implemented.

---

# 10. Critical finding: economic-event filtering has no production provider

## 10.1 What exists

There is an interface:

```csharp
IEconomicEventProvider
```

and test-only provider implementations.

## 10.2 Current problem

The simulator constructs `TradingConditionFilter` without a real event provider.

When users enable:

```text
EconomicEventFilterEnabled
```

the filter reports `Unavailable` and continues.

This is technically honest internally but misleading in the UI because the user may believe high-impact event protection is active.

## 10.3 Required correction

Choose one of these production-safe behaviours.

### Option A — implement provider

Add a versioned provider with cached historical event data.

Requirements:

- event scheduled time;
- currency;
- importance;
- provider version;
- `KnownAt`;
- historical revisions policy;
- cache/data hash;
- no future-known events.

### Option B — reject unsupported configuration

Until a provider is configured:

```text
EconomicEventFilterEnabled = true
    → request validation error
```

Do not silently continue as if protected.

## 10.4 Dashboard health

Display:

```text
Economic event provider:
    Available / Unavailable
Provider version:
    ...
Coverage:
    ...
```

---

# 11. Critical finding: trading sessions are not fully DST-aware

`TradingConditionFilter` uses a timezone for broker rollover.

However, London and New York session classification currently uses fixed UTC hours:

```text
London: 07:00–16:00 UTC
New York: 12:00–21:00 UTC
```

These hours do not remain correct across British and US daylight-saving transitions.

## Required correction

Add explicit IANA zones:

```csharp
Europe/London
America/New_York
```

Define sessions in local exchange/market time.

Convert each timestamp using `TimeZoneInfo`.

Handle periods when US and UK DST transitions occur on different dates.

Add tests around:

- UK spring transition;
- US spring transition;
- UK autumn transition;
- US autumn transition;
- mismatch weeks;
- rollover overlap.

---

# 12. Critical finding: market-regime unsafe inputs are not wired

`MarketRegimeClassifier.Update` supports:

```csharp
spreadAtr
dataQualityOk
```

but `ChartAnnotationEngine` invokes it without those values.

The defaults are therefore:

```text
spreadAtr = null
dataQualityOk = true
```

Consequences:

- `IlliquidUnsafe` cannot be reached because of spread;
- `IlliquidUnsafe` cannot be reached because of market-data quality;
- regime routing and chart bands do not reflect the same safety inputs used by the trading-condition filter.

## Required design

Do not push broker execution concerns directly into the generic annotator.

Add a resolved runtime context:

```csharp
public sealed record AnalysisRuntimeContext
{
    public decimal? ExecutableSpread { get; init; }
    public bool DataQualityOk { get; init; } = true;
    public IReadOnlyList<string> DataQualityIssueCodes { get; init; } = [];
}
```

Extend:

```csharp
IChartAnnotator.ProcessAsync(
    CandleClosedEvent candleEvent,
    AnalysisRuntimeContext? runtimeContext,
    CancellationToken cancellationToken)
```

or add a separate `MarketRegimeRuntimeOverlay`.

Ensure:

```text
regime classifier
trading condition filter
replay
Dashboard
```

use one consistent spread/data-quality interpretation.

Do not double-count the same spread penalty in confidence and risk.

---

# 13. Important finding: anchored value references are calculated but not operational

## 13.1 Current state

`AnalysisSnapshot.ValueReferences` is populated and included in replay contracts.

However:

- `AnalysisChart.vue` does not render them;
- Agents do not use them;
- TradeManager does not use them;
- setup confidence does not use them;
- no Dashboard layer toggle exists.

## 13.2 Required implementation

Add chart layers for:

- session anchored value;
- week anchored value;
- swing anchored value;
- structure-break anchored value;
- optional standard-deviation bands.

Use distinct labels for:

```text
Anchored TWAP
Broker tick-volume VWAP
Exchange-volume VWAP
```

Do not show all anchors simultaneously by default if it makes the chart unreadable.

## 13.3 Agent use

Add optional, independent value-location evidence:

```text
trend pullback returns near value
breakout is excessively stretched from value
price rejects value in trend direction
price loses value during structural deterioration
```

This should be:

- configurable;
- reason-coded;
- included in ablation;
- soft evidence initially.

---

# 14. Important finding: QuantResearch is helper code, not an operational research workflow

## 14.1 What exists

The project contains:

- walk-forward fold creation;
- purged split helper;
- Monte Carlo helper;
- feature-ablation callback runner;
- parameter-grid callback runner;
- confidence calibration;
- trade-management cohort analysis.

## 14.2 What is missing

There is no production research runner that:

- reads simulation outputs;
- creates folds;
- runs simulations;
- selects parameters on validation;
- executes untouched test windows;
- exports reports;
- generates calibration artefacts;
- records data/configuration hashes;
- resumes interrupted experiments.

`FeatureSwitches` is not mapped to actual simulator options.

`FeatureAblationRunner` receives an abstract callback but no implementation in CLI/Dashboard.

## 14.3 Required project

Add:

```text
QuantResearchRunner/
    QuantResearchRunner.csproj
```

or extend `BacktestRunner` with explicit research subcommands.

Recommended CLI:

```bash
dotnet run --project QuantResearchRunner -- walk-forward plan.json
dotnet run --project QuantResearchRunner -- monte-carlo simulation-id
dotnet run --project QuantResearchRunner -- ablation plan.json
dotnet run --project QuantResearchRunner -- sensitivity plan.json
dotnet run --project QuantResearchRunner -- calibrate-setups plan.json
dotnet run --project QuantResearchRunner -- calibrate-management plan.json
```

## 14.4 Research plan

```csharp
public sealed record QuantResearchPlan
{
    public required IReadOnlyList<InstrumentKey> Instruments { get; init; }
    public required IReadOnlyList<string> Strategies { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required WalkForwardPlan WalkForward { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<decimal>> ParameterGrid { get; init; }
    public required FeatureSwitches FeatureSwitches { get; init; }
    public required string OutputDirectory { get; init; }
    public required int Seed { get; init; }
}
```

## 14.5 Artefact outputs

```text
research/{experimentId}/
    plan.json
    manifest.json
    folds.json
    fold-*/validation.json
    fold-*/test.json
    monte-carlo.json
    ablation.json
    sensitivity.json
    setup-calibration.json
    management-calibration.json
    COMPLETE
```

---

# 15. Important finding: feature switches do not control the actual simulator

`FeatureSwitches` contains fields such as:

```text
PriceAction
AdxDmi
RsiRelationship
RegimeRouting
SecondaryTrend
ScaleOut
ProfitFloor
AdaptiveSizing
```

But there is no canonical mapper from these switches into:

- `ChartAnnotationOptions`;
- `ProgressiveStrategyOptions`;
- `PositionManagementOptions`;
- `AdaptiveRiskOptions`;
- `TradingConditionOptions`.

Add:

```csharp
public static class FeatureSwitchMapper
{
    public static BacktestRuntimeOptions Apply(
        BacktestRuntimeOptions baseline,
        FeatureSwitches switches);
}
```

Every switch must be proven to actually disable the feature.

Add test:

```text
baseline
    vs
feature disabled
```

and verify that the manifest and decision evidence reflect the disabled feature.

---

# 16. Important finding: setup and management calibration are not usable from Dashboard/CLI

## 16.1 Existing runtime support

`BacktestRuntimeOptions` supports:

```text
SetupCalibrationArtifact
ManagementCalibrationArtifact
MetaLabelModel
```

## 16.2 Missing operational path

The Dashboard API request does not accept a safe server-owned calibration artefact ID.

The CLI does not expose a complete artefact loader.

No repository validates:

- schema;
- hash;
- strategy version;
- feature schema;
- training period;
- instrument group;
- data hash.

## 16.3 Required calibration repository

Add:

```text
.cache/calibration-artifacts/
```

with server-owned IDs.

```csharp
public interface ICalibrationArtifactRepository
{
    Task<CalibrationArtifactMetadata> StoreAsync(...);
    Task<SetupCalibrationArtifact?> GetSetupAsync(Guid id, ...);
    Task<TradeManagementCalibration?> GetManagementAsync(Guid id, ...);
    Task<IReadOnlyList<CalibrationArtifactMetadata>> ListAsync(...);
}
```

API:

```text
POST   /api/calibrations/setup
POST   /api/calibrations/management
GET    /api/calibrations
GET    /api/calibrations/{id}
DELETE /api/calibrations/{id}
```

Simulation request should accept:

```text
SetupCalibrationArtifactId
ManagementCalibrationArtifactId
```

Never accept arbitrary server-local paths.

---

# 17. Important finding: meta-labelling is only an interface-level integration

A meta-model interface is used by `SafeTradingPipeline`.

However, there is no production model loader or model artefact registry.

Do not claim ML meta-labelling is operational until the following exist:

```text
versioned model artefact
feature schema hash
model loader
calibration metadata
training range
test results
drift metadata
safe inference failure policy
Dashboard/CLI selection
replay model version
```

Initial production model may only:

- reject;
- reduce risk;
- return neutral.

It must not increase above base risk.

---

# 18. Important finding: Dashboard exposes only part of advanced configuration

## 18.1 Present controls

The Dashboard exposes:

- regime enablement;
- ER period;
- regime timing;
- spread thresholds;
- account mode;
- portfolio heat limits;
- correlation settings;
- adaptive risk toggle;
- execution model;
- stress scenario;
- financing toggle;
- one simple equity-protection tier.

## 18.2 Missing controls

Not fully exposed:

- full adaptive-risk schedules;
- regime policy table;
- per-regime management profiles;
- currency-strength options;
- cross-market universe;
- correlation interval;
- correlation risk multipliers;
- economic provider status/version;
- instrument-specific sessions;
- multiple high-watermark tiers;
- per-strategy equity tiers;
- setup calibration artefact selection;
- management calibration artefact selection;
- meta-model selection;
- financing rate catalog;
- real historical bid/ask availability;
- shared portfolio instrument list;
- opportunity ranking weights;
- instrument contract specifications.

Add advanced configuration sections with clear units and safe validation.

Do not expose a control that has no runtime effect.

---

# 19. Important finding: CLI exposes only a subset of runtime capability

The CLI currently exposes selected booleans and headline limits.

It does not expose the full configuration required to reproduce Dashboard simulations.

Required correction:

- support a versioned JSON configuration file;
- allow CLI flags to override JSON;
- write the resolved configuration into output;
- ensure Dashboard can export the same JSON;
- ensure CLI and Dashboard produce identical configuration hashes.

Recommended:

```bash
BacktestRunner --config simulation.json
BacktestRunner --config simulation.json --from ... --to ...
```

---

# 20. Important finding: shared opportunity ranking uses neutral placeholders

Current shared opportunity construction uses values such as:

```text
CorrelationPenalty = 0
CurrencyConcentrationPenalty = 0
SetupQuality = meta probability or neutral 0.5
```

This makes the allocator operational but quantitatively incomplete.

Required real inputs:

```text
calibrated setup expectancy
regime suitability
currency-strength support
spread/ATR cost
correlation penalty
currency concentration penalty
margin consumption
portfolio heat contribution
strategy budget usage
```

Do not treat raw confidence as probability.

When no calibrated quality exists:

```text
use neutral contribution
record quality unavailable
```

Do not silently score it as validated 50% probability.

---

# 21. Important finding: instrument risk specifications are still heuristic

`InstrumentRiskSpec.ForInstrument` currently returns unit-notional defaults for all recognised prefixes.

This is acceptable for many spot FX unit quantities, but insufficient for:

- CFDs;
- indices;
- metals;
- futures;
- broker-specific contract sizing;
- minimum trade units;
- margin rates;
- pip/point values.

Integrate instrument metadata from the Broker/Asset Catalog.

Add fields:

```text
ContractMultiplier
PipSize
PointValue
MarginRate
MinimumQuantity
MaximumQuantity
QuantityStep
AccountCurrencyConversionRequirements
```

The sizing engine must use the selected broker/instrument specification rather than string-prefix heuristics.

---

# 22. Important finding: historical execution realism remains incomplete

## 22.1 Existing work

The code now has:

- execution-model abstraction;
- variable synthetic spread;
- slippage components;
- stress scenarios;
- partial-fill capacity;
- financing model.

## 22.2 Remaining limitations

- OANDA historical source still uses midpoint plus configured/synthetic spread;
- real bid and ask candles are not generally used;
- native OANDA protective-stop amendment is not certified;
- financing rate data is user/configuration supplied;
- historical spread/session calibration is not broker sourced;
- market-impact assumptions remain synthetic.

Required source metadata:

```text
FillModel
SpreadSource
SlippageModelVersion
FinancingSource
BidAskCoverage
SyntheticAssumptions
```

Display these prominently in results.

---

# 23. Important finding: live broker functionality is not certified

Before live deployment, add broker demo certification for:

- position sizing against broker instrument details;
- partial close;
- reduce-only semantics;
- stop amendment;
- target quantity reconciliation;
- OCO behaviour;
- reconnect/reconciliation;
- financing;
- rejected operations;
- duplicate client IDs;
- account netting.

The simulator being deterministic does not prove live broker behaviour.

---

# 24. Important finding: some documentation is stale

`FINAL_PHASE2_VALIDATION.md` still says:

```text
overnight financing/swap is not yet modelled
```

but financing code now exists.

Other documents still describe earlier architectural boundaries that have partially changed.

After implementation, update:

```text
Readme.md
ARCHITECTURE_SIMULATOR.md
VALIDATION.md
FINAL_PHASE2_VALIDATION.md
QUANT_RISK_MTF_VALIDATION.md
SIMULATOR_PHASE4_VALIDATION.md
```

Each limitation must distinguish:

```text
not implemented
implemented but synthetic
implemented but not certified
implemented and tested
```

---

# 25. Important finding: C# completion has not been independently proven

The supplied validation documents state that .NET compilation was not run in the artifact environment.

The implementation now contains many projects and cross-project contracts.

Before additional features, run:

```bash
dotnet --info
dotnet restore TradingHub.slnx
dotnet build TradingHub.slnx -c Release
dotnet test TradingHub.slnx -c Release
```

Because warnings are treated as errors, fix every warning.

Do not bypass build failures by:

- disabling nullable analysis;
- disabling warnings-as-errors;
- excluding new projects;
- weakening tests;
- removing AOT compatibility without explanation.

Also run:

```bash
dotnet publish Simulator.AotSmoke/Simulator.AotSmoke.csproj \
  -c Release \
  -r linux-x64 \
  /p:PublishAot=true
```

where supported.

---

# 26. Required new end-to-end tests

## 26.1 Correlation integration

- two same-direction highly correlated instruments;
- candidate quantity reduced;
- shared cluster heat updated;
- event emitted;
- configuration threshold changes result.

## 26.2 Currency strength

- basket data aligned chronologically;
- leave-out pair;
- differential attached to decision;
- opportunity rank changes;
- missing coverage produces neutral state.

## 26.3 Shared broker account

- two strategies use one account balance;
- combined margin is authoritative;
- same-instrument positions reconcile via virtual lots;
- one strategy partial close does not close another strategy lot;
- account equity equals ledger result;
- no double starting-balance aggregation.

## 26.4 Multi-instrument clock

- frames interleave correctly;
- simultaneous opportunities are collected before ranking;
- one slow instrument does not introduce lookahead;
- missing market data follows configured policy;
- same result in sequential/parallel modes.

## 26.5 Currency exposure

- long GBP/JPY and GBP/USD breach GBP limit;
- long GBP/USD and short GBP/JPY offset only correctly converted portions;
- missing JPY conversion rejects candidate;
- reservation risk included.

## 26.6 Event filter

- enabled with no provider fails validation;
- historical event `KnownAt` after simulated timestamp is ignored;
- provider version appears in manifest;
- blackout delays entry;
- open-position protection continues.

## 26.7 DST sessions

Test transition weeks for London and New York.

## 26.8 Regime runtime context

- high spread creates unsafe regime;
- data-quality failure creates unsafe regime;
- same condition is visible in Agent, replay and Dashboard;
- no contradictory “tradeable regime but rejected for same hidden reason”.

## 26.9 Research workflow

- one small walk-forward experiment;
- test window untouched;
- artefacts written;
- resumable experiment;
- ablation switch actually changes runtime option;
- deterministic seed.

## 26.10 Calibration repository

- upload/store/list/select;
- incompatible schema rejected;
- feature hash mismatch rejected;
- arbitrary path rejected;
- expired/deleted artefact rejected.

---

# 27. Required performance tests

Measure:

```text
1 instrument / 2 strategies
5 instruments / 2 strategies
20 instruments / 2 strategies
50 analysis-only basket instruments
```

Report:

- execution frames/sec;
- annotation updates/sec;
- cross-market updates/sec;
- allocator latency;
- reservation latency;
- memory;
- GC;
- replay size;
- correlation update time.

Do not recompute full correlations on every execution second.

Update correlation only when the configured risk interval closes.

---

# 28. Required operational diagnostics

Add a Dashboard portfolio diagnostics panel:

```text
shared account balance/equity
portfolio heat
pending risk
reserved margin
open positions
virtual lots
net/gross currency exposure
currency stop risk
correlation clusters
pair correlations
capital allocation rank
rejected opportunities
resized opportunities
cross-market data age
currency-strength coverage
economic provider state
calibration/model versions
```

Every admission must explain:

```text
why approved
why resized
why rejected
which limit was approached
which multiplier changed risk
```

---

# 29. Implementation priority

Implement in this order.

## Priority 0 — compile and validate current source

1. restore;
2. build;
3. test;
4. fix real compiler/test failures;
5. preserve baseline hashes.

## Priority 1 — remove misleading disconnected behaviour

1. either wire correlation or hide/disable controls;
2. either provide event provider or reject enabled state;
3. label currency strength unavailable until wired;
4. remove false cluster IDs;
5. expose degraded states.

## Priority 2 — correct shared account

1. authoritative shared broker;
2. virtual lots;
3. one ledger;
4. one margin pool;
5. one safety controller;
6. deterministic attribution.

## Priority 3 — multi-instrument portfolio engine

1. portfolio instrument request;
2. chronological multi-instrument clock;
3. cross-market snapshot;
4. simultaneous opportunity collection.

## Priority 4 — currency and correlation risk

1. conversion snapshot;
2. real currency exposure;
3. correlation engine;
4. cluster heat;
5. allocator penalties.

## Priority 5 — research/calibration workflow

1. research runner;
2. feature mapping;
3. artefact repository;
4. Dashboard/CLI selection;
5. reports.

## Priority 6 — value/context and event integration

1. anchored value chart/use;
2. DST sessions;
3. economic events;
4. currency strength.

## Priority 7 — execution/live certification

1. historical bid/ask;
2. broker details;
3. demo amendment/partial close;
4. financing sources;
5. reconciliation tests.

---

# 30. Required final response from the implementing AI

Return:

1. exact baseline build/test results;
2. files changed grouped by project;
3. which audit findings were fixed;
4. which controls were hidden because runtime support is unavailable;
5. shared-account architecture explanation;
6. multi-instrument clock explanation;
7. correlation implementation and data interval;
8. currency-exposure formula and conversion sources;
9. economic-event provider/version;
10. calibration/research workflow;
11. Dashboard and CLI changes;
12. replay/performance changes;
13. deterministic test results;
14. performance benchmark table;
15. clean source ZIP;
16. SHA-256 checksum;
17. remaining limitations.

Do not report:

```text
implemented
```

when the feature only has a class or unit test but does not affect a real simulation.

---

# 31. Acceptance criteria

The corrective pass is complete only when all are true.

- `dotnet build` passes with zero warnings.
- All .NET tests pass.
- Dashboard clean install/typecheck/build passes.
- Correlation settings change a deterministic shared portfolio result.
- Currency-strength data reaches an Agent decision.
- Shared mode uses one authoritative account.
- Multi-instrument opportunities compete in one frame.
- Currency limits use real exposure rather than 50/50 stop-risk approximation.
- Economic filter cannot claim enabled without a provider.
- Session logic is DST aware.
- Regime spread/data quality are wired.
- Anchored value references are visible and optionally usable.
- Research runner produces walk-forward and calibration artefacts.
- Calibration artefacts are selectable securely.
- Configuration identity includes all new behaviour.
- Sequential and parallel results match.
- No lookahead is introduced.
- Documentation accurately separates synthetic, uncertified and production-tested features.
