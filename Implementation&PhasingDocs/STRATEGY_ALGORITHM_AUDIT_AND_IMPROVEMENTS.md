# Strategy algorithm audit and improvements

**Audit date:** 2026-07-15  
**Scope:** the current `TradingHub` workspace, including its uncommitted working-tree state  
**Purpose:** assess the algorithms that exist, check whether they are used coherently, improve high-confidence strategy and risk defects, and record what still needs validation

## Executive conclusion

The project does **not** use every possible trading algorithm, and it should not try to. There is no finite, universally best set of indicators or strategies. Adding MACD, stochastic oscillators, more moving averages, machine learning, reinforcement learning, and similar features simply because they exist would add correlated signals, parameters, and overfitting risk.

The current system already has a broad, coherent deterministic stack:

- Wilder ATR, RSI, and ADX/DMI; Bollinger bands; same-feed relative volume;
- confirmed non-repainting swings;
- DBSCAN support/resistance zones;
- deterministic RANSAC trendlines and validated price channels;
- market-structure, BOS, CHOCH, retest, rejection, displacement, liquidity-sweep, compression-breakout, and price-leg analysis;
- stateful composite price-action setups;
- role-based multi-timeframe entry logic;
- fixed, cash-risk, and fractional-risk sizing;
- margin, open-risk, pre-trade, and trading-safety controls;
- bracket exits, break-even, ATR/structure trailing, staged reductions, profit floors, MFE giveback, stagnation, momentum-decay, and volatility-exhaustion management;
- completed-candle replay with next-candle order eligibility.

The core indicator mathematics and the main anti-look-ahead design are sound. The most important issues were not missing indicators; they were integration defects: weak ADX was rewarded, contradictory directional evidence had a bullish fall-through, an unused confidence threshold allowed weak triggers, stale/weak higher-timeframe context could arm a trade, a reversal setup could survive an opposing break, target selection could pretend that a farther target was reachable through a nearer obstacle, and risk sizing could fall back to an unsafe fixed quantity.

Those defects have been corrected. Follow-up work also wires the enriched RSI, Bollinger, support/resistance, and relative-volume context directly into agent decisions. The solution builds with no warnings, and all 502 local simulator/unit tests pass. This is an engineering-quality improvement, **not proof of future profitability**.

## What was audited

### Signal and structure algorithms

| Area | Implementation | Verdict | Notes |
|---|---|---|---|
| ATR | `ChartAnnotator/Indicators/AtrState.cs` | Correct | True range and Wilder smoothing are seeded and updated correctly. |
| RSI | `ChartAnnotator/Indicators/RsiState.cs`, `RsiAnalysisState.cs` | Correct and agent-integrated | Wilder smoothing is correct. Momentum plus regular/hidden divergence and convergence are causal because relationships use confirmed swings. Agents now score them by direction. |
| ADX / DMI | `ChartAnnotator/Indicators/AdxState.cs` | Correct math; use improved | DM selection, Wilder sums, DX seed, and ADX smoothing are correct. Strategy/scoring use needed fixes described below. |
| Bollinger bands | `ChartAnnotator/Indicators/BollingerState.cs`, `BollingerAnalysisState.cs` | Correct and agent-integrated | Rolling statistics, normalized bandwidth, percentile, %B, squeeze, release, and expansion are maintained incrementally. Agents now require direction before using them as signal evidence. |
| Relative candle volume | `ChartAnnotator/Indicators/VolumeAnalysisState.cs` | Added and agent-integrated | Uses a rolling median and guarded percentile within one instrument/timeframe and one declared `VolumeKind`. It resets when semantics change. Tick count is explicitly treated as an activity proxy, not traded quantity. |
| Swing pivots | `ChartAnnotator/Structure/SwingDetector.cs` | Correct | A pivot is emitted only after right-side candles close; `PivotTime` and `ConfirmedAt` preserve causal availability. Plateau tie-breaking avoids duplicates. |
| DBSCAN price zones | `SupportResistanceDetector.cs` | Good heuristic and agent-integrated | ATR-normalized distance, temporal splitting, recency-aware role, break/inactivity checks, and bounded history are sensible. Agents now score supporting and opposing zones by candidate direction, strength, and ATR proximity. Parameters remain empirical. |
| RANSAC trendlines | `RansacTrendlineDetector.cs` | Good heuristic | Deterministic candidate search avoids run-to-run jumps. It refits consensus, checks recent evidence and violations, and limits stale lines. |
| Channels | `ChannelDetector.cs` | Good heuristic | Correctly treats a channel as parallel geometry, requires evidence on both sides, checks width/drift/breaks/recency, and removes duplicates. |
| Market structure | `MarketStructureAnalyzer.cs` | Correct for rule-based use | Uses confirmed swings for rising/falling structure and structural breaks. No future pivots are consumed. |
| Atomic price action | `PriceActionAnalyzer.cs` | Correct after fix | BOS/CHOCH, retest, rejection, displacement, sweep, compression breakout, and price-leg metrics use completed data. Calibration can be frozen at evaluation start. |
| Composite setups | `PriceActionSetupComposer.cs` | Correct after fix | Stateful break/CHOCH-retest, sweep-displacement, and sweep-CHOCH sequences now have consistent trigger, timeout, and opposing-break invalidation. |
| Confidence score | `ConfidenceScorer.cs` | Heuristic; corrected | It is a bounded evidence score, not a calibrated probability. Very weak ADX now counts against directional confidence instead of adding confidence. |

### Strategy, risk, execution, and management algorithms

| Area | Verdict | Notes |
|---|---|---|
| Rule-based multi-timeframe entry | Correct after fix | Requires unique timeframes ordered entry → confirmation → trend and no longer repeatedly signals while already positioned. |
| Progressive multi-timeframe state machine | Improved | Uses primary trend, optional secondary context, setup evidence, configurable confirmation consensus, and an entry trigger. Direction conflicts now wait unless strong DMI resolves them. |
| Higher-/lower-timeframe price action | Correct after fix | HTF context arms or vetoes an LTF trigger without projecting HTF candle geometry onto another timeframe. Weak and expired arms are now rejected. |
| Improved structural entry plan | Improved materially | Stop candidates are quality-filtered and ATR-bounded. Targets respect the nearest credible barrier and use a fill buffer. A blocked R:R is rejected rather than manufactured. |
| Legacy strategy | Kept as comparator | Useful as a baseline, but it intentionally has simpler stop/target logic and should not be treated as the preferred live strategy. |
| Position sizing | Correct after fix | Supports fractional/cash/fixed sizing, costs, conversion, contract multiplier, margin caps, portfolio heat, and round-down to broker step. Risk-based failure now fails closed. |
| Pre-trade risk | Strong | Checks stop/target side, reward/risk, position/pyramiding limits, monetary loss, open heat, account state, and safety state. |
| Trade management | Broad and coherent | Break-even can include exit costs; structure trails cannot widen; staged reductions are idempotent; mechanical, fast, main, and thesis layers have deterministic priority. |
| Simulator causality | Good | Analysis is based on completed candles, calibration can freeze before evaluation, and decisions become order-eligible on a later execution candle. |

“Correct” here means that the implementation matches its documented mathematical or rule-based intent and is causal. It does not mean that its thresholds are optimal for a market.

## Changes made in this audit

### 1. Risk-based sizing now fails closed

**File:** `ExecutionManager/Execution/ExecutionCoordinator.cs`

The coordinator previously allowed some failed risk-sizing outcomes to fall back to the strategy's requested quantity. That made the selected cash/fractional risk mode advisory: a missing FX conversion, invalid per-unit loss, missing stop, or below-minimum result could bypass its risk budget.

The fallback was removed. Risk-based modes now reject and journal the sizing reason. Manual quantity remains available only through the explicit `FixedQuantity` mode.

**Why:** a missing conversion is unknown risk, not zero risk. Failing closed also matches the general pre-order control principle of rejecting orders that breach or cannot be checked against preset exposure limits; see the [SEC overview of Rule 15c3-5](https://www.sec.gov/rules-regulations/2011/06/risk-management-controls-brokers-or-dealers-market-access).

### 2. Contradictory trend evidence no longer defaults to Buy

**File:** `Agent/Strategies/ProgressiveStrategyBase.cs`

The old directional test could make both bullish and bearish conditions true—for example, rising structure with a bearish candle below the Bollinger middle. Because the bullish branch was evaluated first, this created a systematic long-side fall-through.

Direction is now split into structural and tactical evidence. When they conflict, the strategy waits unless ADX is strong (`>= 25`) and DMI agrees with structure. An opposing DMI reading with ADX `>= 20` vetoes the candidate.

**Why:** ADX measures trend strength, while +DI/-DI supplies direction. Using them together resolves conflict more coherently than branch ordering.

### 3. Weak ADX is treated as weak evidence

**File:** `ChartAnnotator/Structure/ConfidenceScorer.cs`

Previously, any ready ADX value received a positive score, even near zero. The score now uses strength bands:

- ADX below 15: negative contribution;
- 15–20: slightly negative;
- 20–25: small positive contribution;
- 25 or higher: stronger positive contribution, with an extra reward when strengthening.

**Why:** a calculated ADX value is not automatically evidence of a trend. Low ADX is evidence against a directional trade.

### 4. `MinimumTriggerConfidence` now actually controls triggers

**File:** `ChartAnnotator/PriceAction/PriceActionAnalyzer.cs`

The option was validated but never used. Weak retest, rejection, displacement, compression breakout, sweep, and CHOCH events are now excluded from entry-trigger/composite input and recorded with the diagnostic `TriggerConfidenceBelowMinimum`. Context events such as BOS and measured price legs remain visible.

**Why:** a configuration switch that has no effect is dangerous because research and live behavior diverge from operator intent.

### 5. Opposing breaks invalidate both sweep setup families

**File:** `ChartAnnotator/PriceAction/PriceActionSetupComposer.cs`

An opposing BOS/CHOCH already invalidated sweep→displacement but did not invalidate sweep→CHOCH. The latter could remain armed and later trigger after its reversal thesis had failed. Both families now invalidate consistently.

### 6. Higher-timeframe arms must be recent and strong

**File:** `Agent/Strategies/MultiTimeframePriceActionPolicy.cs`

`HtfArmExpiryBars` was previously unused. It is now validated and enforced. Fallback market structure and PA bias must also meet `MinimumHtfSetupConfidence`; previously their direction alone could arm context.

**Why:** a stale or weak HTF observation should not authorize a fresh LTF order.

### 7. Timeframe roles and position ownership are enforced

**File:** `Agent/Strategies/RuleBasedMultiTimeframeAgent.cs`

The rule-based agent now rejects duplicate or misordered entry/confirmation/trend intervals. It also observes rather than producing another entry when a position already exists for the instrument.

**Why:** using the same snapshot in multiple roles silently double/triple-weights evidence. Repeated entries should not rely solely on a downstream pyramiding rejection.

### 8. Structural stops and targets are more realistic

**Files:** `Agent/Strategies/ImprovedProgressiveAgent.cs`, `Agent/Strategies/ProgressiveStrategyOptions.cs`

Stop improvements:

- prefer a PA setup reference, then entry swing/zone, higher-timeframe structure, a credible channel, and finally ATR fallback;
- ignore weak zones/channels;
- add an ATR buffer beyond invalidation;
- widen micro-stops to 0.35 ATR and ignore oversized structural stops beyond 2.75 ATR;
- retain correct-side and zero-risk validation.

Target improvements:

- consider only credible opposing zones, confirmed swings, and channels;
- buffer targets before the barrier to model fillability;
- ignore isolated micro-swing targets;
- treat the nearest credible obstacle as the available reward;
- reject with `RewardBlockedByStructure` when that obstacle cannot provide minimum R:R;
- use an ATR/minimum-R projection only when no credible obstacle is mapped.

New options (`TargetBufferAtr`, `MinimumSwingTargetDistanceAtr`, `MinimumZoneStrength`, and `MinimumChannelConfidence`) are range-validated.

**Why:** selecting a farther target because the nearer resistance/support gives poor R:R does not improve the trade; it hides the barrier the trade must cross. This change will deliberately reduce trade count when structure blocks the reward.

### 9. Documentation and regression coverage were aligned

`PRICE_ACTION_SETUPS_AND_MTF.md` now documents strict conversion behavior, setup invalidation, HTF expiry, and obstacle-aware targets. Regression tests were added for each corrected failure mode.

### 10. RSI divergence/convergence and Bollinger context now affect entries directionally

**Files:** `Agent/Strategies/RsiBollingerSignalPolicy.cs`, `ProgressiveStrategyBase.cs`, `ProgressiveStrategyOptions.cs`, `RuleBasedMultiTimeframeAgent.cs`, `ChartAnnotator/Structure/ConfidenceScorer.cs`

Before this follow-up, the tools were only partly used:

- RSI relationships added to a direction-neutral chart confidence score and adverse divergence was used by trade-management momentum-decay logic. Because chart confidence has no candidate side, a bearish divergence could inadvertently increase a bullish candidate's total.
- Bollinger squeeze/release/expansion helped the price-action compression-breakout detector, generic confidence, and volatility-exhaustion management. The agents did not explicitly interpret `%B` direction or use RSI relationships as entry confirmation.

The new shared policy evaluates the expected trade direction:

| Evidence | Agent behavior |
|---|---|
| Recent, sufficiently strong aligned regular/hidden RSI divergence or convergence | Adds directional confidence. With aligned RSI momentum or Bollinger location, it may provide the entry trigger. |
| Aligned RSI relationship while RSI is in the matching extreme | Adds extra weight: bullish relationship + oversold, or bearish relationship + overbought. The relationship may trigger without requiring Bollinger confirmation. |
| Strong recent opposing RSI relationship | Vetoes the entry with `OpposingRsiRelationship`. |
| Squeeze release beyond directional `%B` threshold | Adds confidence and may provide a standalone entry trigger. |
| Expansion on the expected side of `%B` | Adds a smaller confirmation boost. |
| Expansion strongly on the opposing side | Vetoes with `OpposingBollingerExpansion`. |
| Unresolved squeeze | No directional score and never triggers by itself. |

The policy is enabled by default for both progressive agents and the reference rule-based agent. Its thresholds and vetoes are configurable through `RsiBollingerSignalOptions`. Every decision explanation records the indicator evidence and the exact confidence adjustment.

The generic `ConfidenceScorer` still exposes RSI/BB diagnostics, but no longer gives a directionless positive score to RSI relationships or an unresolved squeeze. Candidate-specific scoring is owned by the agent, where the trade direction is known.

### 11. Support/resistance and relative volume now confirm agent signals

**Files:** `ChartAnnotator/Indicators/VolumeAnalysisState.cs`, `ChartAnnotationEngine.cs`, `AnalysisModels.cs`, `Agent/Strategies/ZoneVolumeSignalPolicy.cs`, `ProgressiveStrategyBase.cs`, `RuleBasedMultiTimeframeAgent.cs`, `ConfidenceScorer.cs`

The zones were already computed and used for structural rejection, improved stop/target placement, and open-trade management. Volume was carried by broker candles but had no normalized analysis or entry-policy consumer. The generic confidence scorer also rewarded any nearby zone even though it did not know whether the candidate was Buy or Sell.

The new shared policy treats these as **confluence**, not independent direction votes:

| Evidence for a Buy (Sell is mirrored) | Agent behavior |
|---|---|
| Credible support below/around price within the configured ATR radius | Adds a proximity- and strength-scaled boost. Resistance confirms a Sell. |
| Aligned RSI divergence/convergence at that supporting zone | Adds a separate confluence boost and can complete an indicator entry trigger. |
| High/spike relative volume on a completed bullish candle | Adds confirmation. High volume on a bearish candle never becomes bullish evidence. |
| Aligned RSI relationship plus aligned elevated volume | Adds a participation-confluence boost and can complete an indicator entry trigger. |
| Low relative volume | Applies a small penalty; it does not create direction. |
| Strong opposing volume spike | Vetoes the fresh entry. |
| Strong nearby opposing zone | Penalizes the candidate and can veto an indicator-only entry. A directional high-volume Bollinger squeeze release can confirm a breakout through it. |

Zone influence is ATR-bounded and proximity-scaled, so remote resistance/support does not reduce the current entry. Progressive price-action/structural entries do not receive a duplicate early zone veto: the improved agent's existing obstacle-aware target and minimum-R check owns that final barrier decision. This preserves the more precise `RewardBlockedByStructure` result. The simpler rule-based agent can use the zone veto directly.

Volume is normalized against the prior rolling median on the same instrument and timeframe. Percentile can promote a reading only when the median-relative change is also meaningful; this prevents a tiny increase in an unusually flat sample from being called a spike. A change in `VolumeKind` resets the baseline rather than mixing tick counts, base-asset quantity, and last-traded quantity. Unknown-kind volume is ignored by default. OANDA tick volume is labeled **tick activity** in decision reasons and is never represented as actual traded size.

Every confidence change is bounded and included in the decision explanation. The generic chart score retains zone diagnostics at zero directional weight; the candidate-side policy owns the actual contribution.

## Validation performed

### Automated verification

```text
dotnet build TradingHub.slnx -c Release --no-restore
Build succeeded: 0 warnings, 0 errors

Simulator.Tests
442 passed, 0 failed, 0 skipped

TradingHub.UnitTests
60 passed, 0 failed, 0 skipped

Total
502 passed, 0 failed

git diff --check
No whitespace errors
```

The added regression cases cover:

- fail-closed sizing with missing currency conversion;
- contradictory structure/candle evidence without resolving DMI;
- weak versus strong ADX confidence contribution;
- enforcement and diagnostics for the minimum PA trigger confidence;
- aligned versus opposing RSI relationship direction;
- RSI confirmation lifting a qualified agent signal above its confidence floor;
- matching RSI extreme adding weight to aligned divergence/convergence;
- strong opposing RSI divergence vetoing an otherwise aligned agent signal;
- directional Bollinger squeeze release versus an unresolved squeeze;
- same-kind rolling volume baseline, semantic reset, and robust spike classification;
- supporting-zone plus RSI plus aligned-volume confluence lifting an agent signal;
- opposing-zone and opposing-volume veto behavior, including confirmed squeeze breakout exception;
- far opposing zones having no current confidence effect;
- duplicate/misordered timeframe roles;
- suppression of repeat entries with an open position;
- stale and weak HTF context;
- opposing-break invalidation of sweep→CHOCH;
- rejection when nearby structure blocks minimum reward.

### Historical replay

The audit also ran a cached GBP/JPY broker-native replay for 2025-01-01 through 2025-02-01, with five days of warm-up and the current role stack (`2h → 1h → 30m → 15m → 5m`).

Run ID `19fd1f8814eb4db3ade3ff4d409e72b6`, input hash `c4cf22a9f986f73a576141b0649c0d89459f99d8cc8eea1fe5294d21a1618a8a`:

| Strategy | Trades | Win rate | Net P/L | Average R | Profit factor | Maximum drawdown | Commission |
|---|---:|---:|---:|---:|---:|---:|---:|
| Legacy progressive | 19 | 26.3% | -1,685.03 JPY | -0.423R | 0.444 | 1,685.03 JPY | 150.74 JPY |
| Improved progressive | 6 | 33.3% | -163.49 JPY | -0.557R | 0.618 | 258.89 JPY | 47.94 JPY |

The improved strategy was much more selective and had approximately 90% less net loss and 85% less drawdown than the legacy comparator in this sample. However, it still had negative expectancy, a profit factor below 1, and only six trades. The lower loss is encouraging as a risk/selectivity observation, but the sample is far too small to infer an edge. It also highlights that the next research pass should investigate entry timing and setup attribution rather than loosen the new safety gates.

The RSI/Bollinger follow-up was replayed with the same data/configuration (run
`b4817e6cba534cbb8c7ba685c308765c`):

| Strategy | Before RSI/BB agent policy | After RSI/BB agent policy | After profit factor | After maximum drawdown |
|---|---:|---:|---:|---:|
| Legacy progressive | 19 trades / -1,685.03 JPY | 16 trades / -1,663.79 JPY | 0.364 | 1,663.79 JPY |
| Improved progressive | 6 trades / -163.49 JPY | 4 trades / -238.17 JPY | 0.159 | 283.15 JPY |

The policy changed selection, but did **not** improve this small sample: the improved
strategy removed one winner and one loser, and the removed winner was larger. This is
recorded deliberately rather than tuning thresholds to six historical trades. The
integration fixes directionality and makes the indicators usable by the agents; its
profitability remains unproven and must be evaluated across many out-of-sample folds.

The zone/volume follow-up was then replayed with the identical input hash and role
configuration (run `2f09f5544779453da693cc7b027de03f`):

| Strategy | After RSI/BB policy | After zone/volume confluence | Profit factor | Maximum drawdown |
|---|---:|---:|---:|---:|
| Legacy progressive | 16 trades / -1,663.79 JPY | 14 trades / -1,958.67 JPY | 0.232 | 1,958.67 JPY |
| Improved progressive | 4 trades / -238.17 JPY | 4 trades / -238.17 JPY | 0.159 | 283.15 JPY |

The added context did not change the improved strategy's four selected trades in this
sample. It removed two legacy trades and the legacy result became worse. That is useful
negative evidence: the integration behaves as designed, but this single month does not
justify claiming that the default weights improve returns. The defaults were not tuned
to recover those trades.

This replay is a smoke/regression check, not a statistically adequate performance study. It is one instrument and one month, and the current working tree did not provide a clean, identical pre-change baseline. It therefore must not be presented as evidence that the changes increase future returns. The CFTC likewise warns that simulated results have inherent limitations and do not represent actual execution; see its [Regulation 4.41 discussion](https://www.cftc.gov/LawRegulation/FederalRegister/FinalRules/e7-3122.html).

## Algorithms intentionally not added

| Candidate | Decision | Reason |
|---|---|---|
| MACD, stochastic, CCI, extra moving averages | Not added | Mostly correlated transforms of the same OHLC history already represented by RSI, ADX/DMI, Bollinger, structure, and PA. They would increase voting weight and parameters without independent information. |
| Kelly sizing | Not added | Highly sensitive to uncertain win-rate/payoff estimates and can produce unacceptable leverage. Fractional risk plus margin/heat caps is safer. |
| Machine learning / reinforcement learning | Not added | There is not yet a sufficiently broad, purged, out-of-sample research harness or documented feature/label pipeline. Adding a model now would increase selection bias more than confidence. |
| Order-book imbalance, VWAP, volume profile | Not added | Relative candle activity/volume is now used only within a declared feed semantic. These richer methods still require reliable venue-wide volume or depth semantics that are not uniformly available across the configured brokers. |
| Martingale/grid averaging | Not added | Conflicts with bounded risk, stop discipline, and disabled pyramiding. |
| More fixed entry patterns | Not added | The current PA composer already captures continuation and reversal families. The priority is measuring their conditional value, not multiplying patterns. |

## Important remaining limitations

### Priority 0 — required before considering live capital

1. **Walk-forward, out-of-sample validation.** Test multiple years, instruments, spread regimes, and session types with frozen parameters. Report trade count, expectancy in R, drawdown, profit factor, turnover, exposure, tail loss, and stability by fold. Selecting the best of many backtests creates material overfitting risk; the relevant methodology is discussed in Bailey et al., [The Probability of Backtest Overfitting](https://doi.org/10.21314/JCF.2016.322).
2. **Broker-specific contract specifications.** `InstrumentRiskSpec.ForInstrument` is a reasonable FX/crypto default, but CFDs, futures, metals, indices, and broker-specific contracts need explicit multipliers, minimums, steps, margin rules, and currency conversions. Reject unknown specifications rather than guessing.
3. **Execution stress.** Re-run with actual bid/ask candles where available, wider and time-varying spread, slippage, gaps, latency, rejects, partial fills, and stop/target collision rules. A midpoint replay with synthetic costs cannot certify fill quality.
4. **Paper-trading reconciliation.** Confirm that protective orders, amendments, partial reductions, and reconnect/reconciliation behavior match each real broker before enabling opening orders.

### Priority 1 — highest-value research additions

1. **Portfolio allocator.** The current comparative simulator isolates accounts. A multi-instrument live system should cap aggregate heat, group correlated instruments/currencies, reserve shared margin, and rank simultaneous signals.
2. **Explicit regime policy.** The components already measure ADX, Bollinger width, structure, and volatility. Validate a small trend/range/high-volatility regime gate that changes which existing setup family is eligible, rather than adding more raw indicators.
3. **Parameter sensitivity and selection accounting.** Run coarse neighborhoods around thresholds and record every tried configuration. Prefer wide stable plateaus over a single optimum; add walk-forward selection, probability-of-overfit/deflated-Sharpe analysis, and block/bootstrap trade-order stress.
4. **Volatility-scaled portfolio exposure.** Per-trade fractional risk already reacts to stop distance, but portfolio exposure could explicitly fall as aggregate volatility rises. This is a research candidate, not a guaranteed improvement; see Moreira and Muir, [Volatility Managed Portfolios](https://www.nber.org/papers/w22208).
5. **Setup attribution.** Report performance separately for break-retest, CHOCH-retest, sweep-displacement, sweep-CHOCH, direction, session, timeframe, and regime. Disable families that are persistently negative out of sample instead of blending all events into one score.

### Priority 2 — only after the validation foundation exists

- calibrated probability estimates for setup success;
- purged/embargoed cross-validation for any learned model;
- news/calendar risk gates if the deployment venue and instrument need them;
- true volume/depth features only for feeds with trustworthy, consistent semantics.

## Recommended decision

Use `ImprovedProgressiveAgent` as the research candidate and keep `LegacyProgressiveAgent` as a frozen comparator. Do not tune thresholds against the single replay in this audit. First create locked training/validation/test periods and execution-stress scenarios; then accept a change only when it improves risk-adjusted results across folds and instruments without relying on a small number of trades.

The strategy is now safer and internally more consistent, but the correct next improvement is **better validation and portfolio control**, not a larger indicator collection.
