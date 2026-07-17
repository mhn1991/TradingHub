# TradingHub Agent Subsystem — Independent Foundation Audit

## Role

Act as a principal quantitative-trading architect, senior .NET engineer, time-series validation specialist, and adversarial code reviewer.

Perform a deep independent audit of the **TradingHub Agent subsystem and every component directly involved in producing an Agent decision**.

The objective is to determine whether the Agent foundation is correct, deterministic, explainable, environment-neutral, and safe to build upon before adding PostgreSQL persistence, wider live execution, or autonomous trading.

This is not a style review. Trace the actual runtime code.

Do not trust implementation plans or reports without verifying their claims against the source.

---

# Input handling

Use the latest TradingHub source archive attached to the task.

Review any attached plans, audits, configuration notes, and reports as supporting context, but treat source code as authoritative.

If multiple source archives are attached:

1. identify the newest intended implementation;
2. explain how it was selected;
3. avoid mixing versions except for regression comparison;
4. state exactly which source tree was audited.

Do not depend on fixed filenames.

---

# Primary question

Answer:

> Is the TradingHub Agent subsystem a solid, coherent foundation on which RiskManager, PortfolioManager, ExecutionManager, TradeManager, persistence, and autonomous demo trading can safely be built?

Trace this complete path:

```text
Market data
→ completed-candle handling
→ multi-timeframe aggregation
→ ChartAnnotator
→ indicators and structure
→ regime classification
→ Agent state machine
→ feature policy
→ setup calibration
→ meta-label model
→ trading-condition filter
→ neutral trade candidate
```

Also audit its boundaries with:

```text
Simulator
LiveTrading
QuantResearch
RiskManager
PortfolioManager
TradeManager
ExecutionManager
```

---

# Core architectural principles

## Environment neutrality

The Agent and decision-domain components must not know whether they run in:

```text
Simulation
Historical replay
Unit tests
Fake broker
Observe-only live mode
Shadow mode
Manual demo mode
Autonomous demo mode
Future real-money mode
```

Look for direct and indirect leakage:

- `IsLive`, `IsDemo`, or `IsSimulation` flags;
- broker-specific models or IDs;
- direct broker calls;
- account state inside the Agent;
- host execution modes attached to neutral candidates;
- environment variables read by domain services;
- separate live and simulator Agent constructors;
- duplicated live-side strategy configuration;
- simulator-only timing or fill assumptions inside Agent logic.

Environment-specific behaviour should remain in adapters and orchestration layers.

## Responsibility boundaries

The Agent may own:

- setup discovery and setup state;
- directional intent;
- setup confirmation and invalidation;
- entry intent;
- proposed stop and target;
- raw confidence and structured explanation.

The Agent must not own:

- account balance or margin;
- final quantity;
- portfolio allocation;
- broker submission or retry;
- reconciliation;
- account-level safety;
- database persistence;
- calibration training;
- artifact promotion;
- live/demo mode selection.

Verify these boundaries from real call paths.

## Determinism

Given identical ordered market snapshots, configuration, artifacts, and initial state, the Agent must emit identical:

- decisions;
- reason codes;
- stop and target;
- confidence;
- candidate ID;
- state transitions.

Thread scheduling, network response order, logging, UI connections, and dictionary ordering must not alter results.

## Completed-candle discipline

Strategic decisions must use information available at decision time.

Live quotes may support executable price, spread, candidate expiry, trade P/L, MFE/MAE, and emergency safety, but must not silently create unfinished strategic indicator values.

---

# Required architecture map

Identify every project, class, interface, factory, configuration object, and state object involved in Agent decisions.

Include at least:

```text
Agent
TradingCore
ChartAnnotator
indicator utilities
Calibration
meta-label components
trading-condition components
Simulator Agent construction
LiveTrading Agent construction
QuantResearch artifact generation
API/Dashboard configuration mapping
```

For each component, document:

- responsibility;
- inputs and outputs;
- mutable state;
- lifetime;
- thread-safety assumptions;
- dependencies;
- whether shared by simulator and live;
- whether it contains environment-specific knowledge.

Flag circular dependencies, duplicate abstractions, misplaced logic, and Agent behaviour implemented outside the Agent subsystem.

---

# Construction-parity audit

Trace how each Agent instance is constructed in:

```text
Simulator
QuantResearch
Live observe mode
Live shadow mode
Manual demo mode
Autonomous demo mode
Tests
```

Verify that one authoritative construction path is used.

Compare:

- strategy options;
- timeframe plans;
- feature switches;
- ChartAnnotator options;
- calibration artifacts;
- meta-model artifacts;
- regime settings;
- trading conditions;
- stop/target settings;
- confidence thresholds;
- fallback behaviour.

Classify every difference as:

```text
Intentional deployment difference
Configuration drift
Implementation defect
Unable to determine
```

Produce a construction-parity table.

---

# Agent state-machine audit

Audit Legacy and Improved Agents independently.

Document all actual states and transitions, including concepts such as:

```text
Observe
Potential setup
Scoped setup
Awaiting confirmation
Awaiting entry
Candidate emitted
Invalidated
Expired
Reset
```

Use the code's real state names.

For every transition record:

- triggering interval;
- required evidence;
- condition;
- retained state;
- invalidation;
- expiry;
- reset;
- emitted reason code.

Check for:

- unreachable or terminal states;
- stale setup state;
- setup state crossing sessions incorrectly;
- state shared across instruments or strategies;
- repeated emission of one setup;
- candidate-ID instability;
- long/short asymmetry;
- one candle causing multiple invalid transitions;
- lower-timeframe noise overwriting higher-timeframe thesis;
- diagnostics lost during reset.

Produce a Mermaid state diagram for each Agent.

---

# Multi-timeframe audit

For each Agent identify:

- trigger interval;
- primary trend interval;
- secondary trend interval;
- setup interval;
- confirmation interval;
- entry interval;
- exact event that causes evaluation.

Verify:

1. every required interval is available;
2. higher intervals are built only from completed lower candles;
3. higher-timeframe values are invisible before close;
4. each interval close is emitted once;
5. timestamps align correctly;
6. reconnect and catch-up do not duplicate decisions;
7. warm-up satisfies every dependency;
8. all required snapshots belong to a coherent version;
9. stale higher-timeframe snapshots are not combined incorrectly;
10. daylight-saving changes do not distort canonical market intervals.

Create a concrete timestamp example showing when 5m, 15m, 1h, and 2h information becomes legally available.

---

# Lookahead and leakage audit

Check specifically for:

- unfinished candles;
- candle high/low used before close;
- future-confirmed swings exposed at pivot time;
- future bars used in divergence or structure confirmation;
- centered rolling windows;
- trendlines or channels fitted with future points;
- DBSCAN zones built from future observations;
- higher-timeframe candles visible early;
- cross-market values from later timestamps;
- decision features replaced by fill-time values;
- future outcome fields visible to Agent code;
- shared precomputed analysis containing later data.

For swings, pivots, divergence, zones, channels, RANSAC trendlines, and market structure, document:

```text
Observation time
Confirmation time
First legal availability time
Stored annotation timestamp
Timestamp used by Agent
```

Classify each finding:

```text
Confirmed leakage
Likely leakage
Possible but guarded
No leakage found
Unable to determine
```

Include exact files and methods.

---

# Indicator and ChartAnnotator interaction audit

Review all evidence used by the Agents, including where present:

- ATR;
- RSI and divergence/convergence;
- Bollinger Bands and squeeze/expansion;
- DMI/ADX;
- Efficiency Ratio;
- Donchian;
- swings;
- support/resistance zones;
- DBSCAN clustering;
- RANSAC trendlines;
- channels;
- market structure;
- price-action patterns;
- regime;
- currency strength;
- correlation;
- volatility and liquidity state.

For each document:

- source timeframe;
- lookback and warm-up;
- output;
- quality/confidence;
- missing-data behaviour;
- whether missing evidence is neutral or rejecting;
- other components checking the same property.

Evaluate overlaps such as:

```text
Primary trend vs secondary trend
Trend filters vs DMI/ADX
Trend filters vs regime
ATR vs volatility regime
Bollinger squeeze vs volatility regime
RSI divergence vs momentum confirmation
Price action vs structure confirmation
Zones vs channels
Session filter vs liquidity/spread filters
```

Classify each relationship:

```text
Complementary
Intentional confirmation
Redundant
Contradictory
Excessively restrictive
Unclear
```

Do not remove a filter merely to increase trade count.

---

# Gate-stacking and trade-starvation audit

Build the real decision funnel in exact code order.

For example:

```text
Data readiness
→ trend
→ secondary trend
→ setup
→ confirmation
→ entry trigger
→ price action
→ regime
→ setup calibration
→ meta-label
→ trading conditions
→ candidate
```

For every gate document:

- input;
- threshold;
- hard reject, delay, or soft reduction;
- reason code;
- configuration source;
- feature switch;
- diagnostic visibility;
- overlap with another gate.

Determine whether low trade frequency comes from:

- valid selectivity;
- duplicated filters;
- contradictory conditions;
- missing evidence treated as rejection;
- stale setup state;
- unreachable combinations;
- configuration drift;
- dead feature switches;
- regime/condition filtering applied twice.

Produce a funnel matrix showing where candidates are lost.

---

# Collision and obscuration analysis

This section is mandatory.

For every important pair of components determine whether they:

```text
Complement
Partially overlap
Duplicate
Contradict
Obscure diagnostics
Create ordering dependence
```

At minimum review:

```text
Primary trend vs secondary trend
Trend vs DMI/ADX
Trend vs regime
ATR vs volatility regime
Bollinger squeeze vs volatility regime
RSI divergence vs momentum logic
Price action vs structure
Zones vs channels
Agent confidence vs setup calibration
Setup calibration vs meta-model
Meta-model vs expected-R threshold
Regime spread limits vs trading-condition spread limits
Session vs liquidity filtering
Agent invalidation vs TradeManager exits
Agent confidence vs downstream risk multiplier
```

For each overlap explain:

- why both exist;
- unique information from each;
- authoritative component;
- whether ordering changes the result;
- whether both outcomes remain observable;
- whether simplification is justified.

---

# Feature-switch audit

Enumerate every feature switch and trace:

```text
Configuration
→ serialization
→ mapper
→ runtime policy
→ actual decision branch
→ diagnostics
```

Verify:

- every switch affects runtime behaviour;
- false genuinely bypasses the feature;
- true does not enable incomplete functionality silently;
- simulator and live receive the same value;
- no dead or inverted switches;
- features are not evaluated before their switch;
- cached results do not survive disabling incorrectly;
- unrelated behaviour is not changed.

Produce a switch table with defaults, code path, tests, and issues.

---

# Setup calibration audit

Verify:

- explicit versioned feature schema;
- decision-time features only;
- no future outcome features;
- compatible strategy and timeframe;
- content/schema hash validation;
- neutral behaviour for insufficient buckets unless policy says otherwise;
- bounded effects;
- explicit artifact promotion;
- simulator/live parity;
- walk-forward and purged validation where claimed;
- no fitting on the same evaluation window.

Explain exactly whether setup calibration:

- rejects candidates;
- changes confidence;
- changes risk;
- overlaps with Agent confidence;
- overlaps with the meta-model.

---

# Meta-model audit

Verify:

- decision-time feature generation;
- clear label definition;
- out-of-fold inputs where necessary;
- no in-sample setup prediction leakage;
- probability calibration;
- Brier score or equivalent tracking;
- minimum sample coverage;
- neutral missing-bucket behaviour;
- multiplier clamped to `0..1`;
- no risk increase above base policy;
- model version and artifact recorded;
- simulator/live parity;
- controlled activation boundary;
- open positions retaining original policy revision.

Determine whether the meta-model complements setup calibration or simply filters the same evidence twice.

---

# Regime and trading-condition audit

Trace every use of regime in:

- setup eligibility;
- confidence;
- stop/target;
- risk;
- management;
- spread handling.

Verify regime uses available data only and does not duplicate trend logic excessively.

Audit trading conditions:

- allowed sessions;
- rollover blackout;
- pre-weekend protection;
- spread/ATR soft and hard limits;
- stale-data limits;
- missing ATR/spread behaviour;
- economic-event filtering;
- risk multipliers;
- delay versus rejection semantics.

Verify shared defaults across every path:

```text
TradingConditionsEnabled = true
AllowedSessions = Asian, London, NewYork, LondonNewYorkOverlap
ConditionSoftSpreadAtr = 0.25
ConditionHardSpreadAtr = 0.50
RegimeSoftSpreadAtr = 0.25
RegimeHardSpreadAtr = 0.50
RolloverBlackoutMinutesBefore = 15
RolloverBlackoutMinutesAfter = 15
EconomicEventFilterEnabled = false
```

Search for any fallback to `0.15/0.30`.

Verify empty sessions fail fast and `DelayEntry` is not described as truly deferred unless candidates are persisted and revalidated.

---

# Stop, target, and reward/risk audit

For every candidate path verify:

- Buy stop below entry;
- Sell stop above entry;
- Buy target above entry;
- Sell target below entry;
- positive stop and target distances;
- valid reward/risk;
- price precision;
- decision-time-only structure;
- spread-aware minimum distance;
- no candidate without an initial stop;
- explicit fallback behaviour;
- long/short symmetry.

Test edge cases:

- tiny or huge ATR;
- missing swing or zone;
- narrow market range;
- gap/open discontinuity;
- extreme spread;
- decimal rounding boundaries.

---

# Confidence and scoring audit

List every score:

```text
Raw Agent confidence
Multi-timeframe alignment
Price-action score
Regime score
Setup-calibration result
Meta probability
Expected R
Risk multiplier
```

For each explain:

- meaning and range;
- whether it is a calibrated probability;
- rejection effect;
- risk effect;
- persistence/audit fields;
- overlap with other scores.

Look for incomparable scores being added or multiplied, arbitrary confidence treated as probability, repeated thresholds, and asymmetric long/short calculations.

---

# Downstream boundaries

## Agent to RiskManager

The neutral candidate should contain trading evidence, not account or broker state.

Verify it includes enough data to audit the decision but excludes:

```text
Account balance
Final order quantity
Portfolio approval
Broker order IDs
Environment mode
Broker-specific requests
Database entities
Retry state
```

RiskManager must independently validate geometry, risk, costs, conversion, and hard caps.

## Agent to TradeManager

Verify entry and management responsibilities are separate.

TradeManager should own break-even, scale-out, trailing, profit floors, MFE giveback, stagnation, deterioration, and management exits.

Identify any hidden management inside Agent code, including reverse-signal exits, stop movement, size reduction, P/L tracking, or account drawdown.

Create a precedence table for:

```text
Agent thesis invalidation
TradeManager action
Account-safety action
Manual action
Broker stop
Broker target
```

## Agent to PortfolioManager

Market evidence may influence the candidate; portfolio capacity and competition must remain downstream.

Verify the Agent does not consume account-wide heat or choose which simultaneous opportunity receives capital.

---

# State isolation and concurrency audit

Verify intended lifetime:

```text
one Agent instance per instrument × strategy assignment
```

Check for:

- static mutable state;
- singleton Agents reused across instruments;
- shared mutable collections;
- concurrent calls into one Agent;
- cached analysis overwritten by another market;
- mutable snapshots;
- dictionary-order dependence;
- duplicate evaluation after reconnect;
- policy activation racing with evaluation;
- cancellation during a state transition;
- state reset while diagnostics are read.

For every mutable state object document owner, writers, readers, synchronization, and checkpoint needs.

Produce a concurrency/state-ownership diagram.

---

# Warm-up and readiness audit

For every required component document:

- required samples;
- available samples;
- timeframe;
- hard or optional requirement;
- missing-data behaviour.

Cover indicators, annotations, regime, cross-market evidence, calibration, and meta-model feature coverage.

Verify the Agent cannot trade before hard dependencies are ready and optional missing evidence does not become an accidental hard rejection.

Calibration training must remain in QuantResearch and must not run inside normal live warm-up.

---

# Diagnostics and observability audit

Determine whether the Agent funnel exposes per-instance structured diagnostics for:

```text
Evaluations
Observe decisions
Buy candidates
Sell candidates
Trend rejections
Setup rejections
Confirmation rejections
Price-action rejections
Regime rejections
Setup-calibration rejections
Meta-model rejections
Trading-condition rejections
Invalid stop/target
Duplicate decisions
Missing data
Last candidate
Last error
```

Verify diagnostics do not alter logic and reason codes remain distinct enough that subsystems do not obscure one another.

Confirm the resulting events can later be persisted in PostgreSQL without coupling the Agent directly to the database.

---

# Build and test requirements

Discover the actual solution and project names.

Run:

```bash
dotnet --info
dotnet restore <solution>
dotnet build <solution> -c Release
dotnet test <solution> -c Release
```

Run Agent, ChartAnnotator, TradingCore, Calibration, RiskManager, Simulator, and LiveTrading tests separately when useful.

If the .NET SDK is unavailable, state that clearly. Do not imply compilation or tests passed.

Do not run broker-write tests for this Agent audit.

---

# Required test coverage assessment

Assess or add tests for:

## Construction parity

- simulator and live build equivalent Agents;
- promoted policy reproduces simulator settings;
- hashes and artifacts match;
- defaults do not drift.

## Determinism

- identical replay gives identical decisions;
- sequential and parallel processing agree;
- instrument ordering does not matter;
- repeated replay cannot duplicate candidates.

## State machine

- every transition, reset, expiry, and invalidation;
- opposite-direction setup;
- session boundary;
- stale setup;
- duplicate trigger;
- reconnect catch-up.

## Timeframes

- higher timeframe unavailable before close;
- trigger executes once;
- mixed snapshot versions rejected;
- missing intervals fail safely;
- no-trigger markets do not block unrelated epochs.

## Leakage

- swings and divergence visible only after confirmation;
- channels, zones, and trendlines exclude future data;
- cross-market timestamps are respected;
- outcome labels are never visible.

## Long/short symmetry

- mirrored data creates mirrored decisions;
- stop/target geometry remains valid.

## Missing evidence

- ATR or spread unavailable;
- regime unavailable;
- missing swing/zone;
- insufficient calibration bucket;
- incomplete cross-market coverage.

## Feature switches

- each switch changes only its intended path;
- no dead or inverted switches.

## Calibration/meta-model

- incompatible artifacts rejected;
- missing buckets neutral;
- multipliers bounded;
- audit reasons preserved.

## Candidate integrity

- deterministic IDs;
- valid geometry;
- mandatory stop;
- no duplicate emission;
- no broker/environment fields.

---

# Production-readiness matrix

Rate each area as:

```text
Not implemented
Scaffolded
Implemented but not wired
Wired but disabled
Enabled but unverified
Verified in deterministic tests
Verified in simulator/live parity
Foundation-ready
```

Areas:

```text
Agent construction
Legacy Agent
Improved Agent
State isolation
Multi-timeframe aggregation
Completed-candle timing
Chart annotation
Swing confirmation
Zones/channels/trendlines
Regime classification
Feature switches
Setup calibration
Meta-labeling
Trading conditions
Candidate generation
Stop/target logic
Confidence scoring
Diagnostics
Shadow evaluation
Simulator/live parity
Policy promotion
Lookahead protection
Concurrency safety
Warm-up readiness
```

---

# Issue format

For every confirmed issue provide:

```text
ID
Title
Severity
Confidence
Subsystem
Files and methods
Observed behaviour
Expected behaviour
Failure scenario
Why it matters
Recommended correction
Required test
```

Severity:

```text
Critical
High
Medium
Low
Informational
```

Reserve Critical for issues such as confirmed lookahead, cross-instrument state contamination, duplicate/contradictory decisions, invalid stop direction, non-deterministic autonomous decisions, environment-dependent Agent behaviour, or unsafe candidate output.

---

# Required diagrams

Produce Mermaid diagrams for:

1. Agent subsystem dependency graph;
2. complete decision pipeline;
3. Legacy Agent state machine;
4. Improved Agent state machine;
5. multi-timeframe availability;
6. calibration and meta-label flow;
7. simulator/live construction parity;
8. concurrency and state ownership;
9. candidate funnel;
10. Agent/RiskManager/TradeManager/PortfolioManager boundaries.

Diagrams must reflect actual code and highlight disconnected, duplicated, or conflicting paths.

---

# Required deliverables

Create a primary Markdown audit report containing:

1. executive verdict;
2. audited source identification;
3. architecture map;
4. dependency findings;
5. construction parity;
6. Legacy Agent analysis;
7. Improved Agent analysis;
8. state-machine findings;
9. multi-timeframe findings;
10. ChartAnnotator and indicator findings;
11. gate-stacking findings;
12. collision and obscuration analysis;
13. feature-switch findings;
14. setup-calibration findings;
15. meta-model findings;
16. regime and trading-condition findings;
17. stop/target and confidence findings;
18. leakage findings;
19. concurrency and state findings;
20. downstream-boundary findings;
21. diagnostics findings;
22. build and test results;
23. production-readiness matrix;
24. prioritized corrective plan;
25. diagrams;
26. final verdict.

Also create a compact Markdown issue register ordered by severity and implementation priority.

Use descriptive filenames based on the repository and audit date. Do not rely on input filenames.

---

# Code-change policy

Audit first.

Do not begin by rewriting the Agent.

After the audit:

1. implement only confirmed defects;
2. preserve environment neutrality;
3. preserve domain boundaries;
4. do not loosen filters merely to create more trades;
5. add deterministic tests for every fix;
6. document behavioural changes;
7. return a corrected source ZIP;
8. return a Markdown fix report;
9. return a SHA-256 checksum.

Treat strategy choices separately from correctness defects and do not change them automatically.

---

# Final verdict questions

End with direct answers:

1. Is the Agent subsystem fundamentally sound?
2. Are Legacy and Improved Agents deterministic?
3. Are Agent instances isolated by instrument and strategy?
4. Are simulator and live construction paths equivalent?
5. Are the Agents environment-neutral?
6. Is completed-candle timing correct?
7. Is there confirmed or likely lookahead leakage?
8. Are swings, divergence, zones, channels, and trendlines exposed only when legally available?
9. Do the decision subsystems complement each other?
10. Which components duplicate, contradict, or obscure one another?
11. Is gate stacking causing accidental trade starvation?
12. Are all feature switches wired correctly?
13. Are setup calibration and meta-labeling complementary?
14. Are regime and trading-condition rules consistent?
15. Are stops, targets, and reward/risk valid for both directions?
16. Can every final candidate be fully explained?
17. Does the Agent avoid RiskManager, PortfolioManager, ExecutionManager, and TradeManager responsibilities?
18. What are the five highest-priority corrections?
19. What must be fixed before implementing PostgreSQL persistence around this subsystem?
20. Is the Agent foundation strong enough to continue building the complete agentic trading platform?

Be critical and evidence-based.

Do not treat interfaces, comments, plans, or test names as proof that runtime behaviour is wired.

Do not claim correctness without tracing the code.

Do not add indicators, models, or new Agents unless a confirmed architectural gap requires them.

The final report must let another senior engineer understand exactly how the Agent reaches a decision, how each supporting subsystem affects it, whether those components cooperate correctly, and whether the foundation is safe to build upon.
