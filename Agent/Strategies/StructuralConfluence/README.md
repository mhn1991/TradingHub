# Structural Confluence Agent

## Purpose of this document

This document explains the Structural Confluence agent in trading and risk-management terms. It is intended for finance, risk, product, operations, and engineering readers who need to understand:

- what evidence the agent uses;
- how it moves from market observations to a trade proposal;
- how competing setups are resolved;
- where stops and targets come from;
- what happens to a proposal before an order reaches the broker;
- how an open position is managed; and
- which controls, assumptions, and limitations matter when reviewing results.

It describes the current repository implementation. Values shown as defaults can be replaced by the selected runtime profile, so a production or backtest report should always identify the exact resolved configuration and policy hashes used for that run.

## Executive summary

The Structural Confluence agent is a deterministic, rules-based strategy. Its primary job is to find a structural trading setup, prove that the setup has a valid trigger, and propose an entry, protective stop, and target policy. It does **not** have final authority to decide position size or place an order by itself.

At a high level, it works as follows:

1. Confirm that all required market analysis is complete and time-consistent.
2. Build a single evidence packet from higher-timeframe context, setup structure, and lower-timeframe execution evidence.
3. Let each enabled playbook independently assess that evidence.
4. Reject candidates that fail a mandatory condition, have weak geometry, or offer insufficient reward relative to risk.
5. Resolve agreement or conflict between valid playbooks.
6. Emit either an auditable trade proposal or an `Observe` decision with a reason for not trading.
7. Pass any trade proposal through trading-condition, sizing, portfolio-risk, safety, and broker-execution controls.
8. After entry, manage the position using its original risk, structural thesis, profit protection, and—when enabled—an adaptive target plan.

The central principle is that structural evidence may justify a trade, but it cannot bypass risk controls. Downstream controls can reduce or reject a trade; they cannot increase it beyond the strategy request.

## Where the agent sits in the trading system

```text
Completed market analysis
        ↓
Structural Confluence agent
  evidence → playbooks → geometry → arbitration
        ↓
Trade proposal or Observe decision
        ↓
Optional condition/calibration/meta-model filters
        ↓
Position sizing and central portfolio reservation
        ↓
Final pre-trade risk and broker-safety checks
        ↓
Broker order
        ↓
Position manager: thesis, stop, reductions, exits
```

This separation is important when attributing a decision:

| Question | Primary owner |
|---|---|
| Is there a structurally valid setup? | Structural Confluence agent |
| Is the market currently tradable under configured conditions? | Trading-condition and safety policies |
| How much can be traded? | Position sizing and portfolio risk |
| Can the order safely be submitted? | Pre-trade risk and execution coordinator |
| Should an open position be protected, reduced, or closed? | Position manager |

## 1. Information used by the agent

The agent consumes completed analysis rather than deriving every indicator directly from raw candles. Its upstream inputs can include:

- trend and market-structure assessments;
- supply and demand zones;
- swing highs and lows;
- liquidity pools, sweeps, and accepted breaks;
- lower-timeframe price-action events;
- ATR, CCI, RSI, Bollinger, ADX/DMI, and efficiency measures;
- current bid/ask spread; and
- the presence of an existing position for this strategy and instrument.

The playbooks themselves are technical and price-structure based. They do not make a fundamental, macroeconomic, or news forecast.

### Timeframe roles

The default hierarchy is:

| Role | Default | Decision purpose |
|---|---:|---|
| Context | 1 hour | Directional backdrop and structural regime |
| Setup | 15 minutes | Zones, liquidity, sweeps, breaks, and setup location |
| Trigger | 5 minutes | Entry timing, reaction, and indicator confirmation |

Additional context intervals can be configured. The trigger must be finer than the setup interval, and the setup must be finer than the main context interval.

### Readiness and no-look-ahead controls

Before evaluating a setup, the agent requires every configured interval to be available for the same instrument. Evidence is filtered by its `AvailableAt` time, and analysis or candle timestamps later than the evaluation time are excluded. This is intended to prevent future information from leaking into a historical or live decision.

If the required evidence is incomplete, the result is `Observe`, not a best-effort trade.

The agent also maintains an evaluation epoch per instrument:

- repeating the same timestamp and analysis version returns the same decision;
- an older timestamp or version is rejected as stale; and
- an existing open position for the same strategy and instrument prevents a new entry.

This makes evaluation reproducible within the running process and prevents duplicate signals. The epoch and playbook lifecycle stores are currently in memory, so process restart and state-recovery procedures remain operational considerations.

## 2. Evidence packet and context assessment

The evidence factory converts the separate timeframe analyses into one point-in-time packet for every playbook. This prevents each playbook from applying different time filters or interpreting the same upstream snapshot independently.

The higher-timeframe context score is built from the available trend, breakout, and market-structure strength measures across the configured context intervals. It is used as a bounded adjustment to setup confidence rather than being treated as a direct probability of success.

The setup interval supplies the structural location and catalyst: for example, a zone, a liquidity sweep, or an accepted break. The trigger interval supplies the recent price-action event and the execution indicators used to time entry.

## 3. Playbooks: the trading hypotheses

Each playbook represents a different market hypothesis. It independently returns either a candidate or a structured explanation of why it is dormant, waiting, invalid, or expired.

Three structural playbooks are enabled by default. A fourth indicator-only playbook exists but is disabled by default.

| Playbook | Default | Trading hypothesis | Typical direction |
|---|---:|---|---|
| Liquidity Sweep Reversal | On | Price takes resting liquidity, rejects the break, and reverses | Sell-side sweep → long; buy-side sweep → short |
| Supply/Demand Pullback | On | Price revisits a usable zone and resumes from it | Demand → long; supply → short |
| Liquidity Break/Retest | On | Price accepts beyond a liquidity level, retests it, and continues | Buy-side break → long; sell-side break → short |
| Indicator Confluence | Off | Trend/momentum/volatility indicators align without a structural anchor | Direction follows indicator agreement |

### 3.1 Liquidity Sweep Reversal

This playbook looks for a failed attempt to continue through a known liquidity pool.

Its decision sequence is:

1. A liquidity pool must have existed before the sweep.
2. The pool must meet the permitted type and minimum quality rules.
3. Price must sweep beyond the level and then close back inside it.
4. The rejection must have sufficient strength and a plausible penetration depth.
5. The event must still be fresh and must not have been superseded by an accepted break.
6. A nearby supply or demand zone is preferred as supporting location evidence.
7. A recent lower-timeframe trigger must agree with the reversal direction.
8. Optional confirmation, including CCI and micro-structure evidence, can strengthen or weaken the candidate.

The protective stop is placed beyond the sweep extreme. If a supporting zone extends farther, its invalidation edge is used instead. An ATR-based buffer is then added so ordinary noise around the exact level does not immediately invalidate the trade.

A reversal can still be considered when the higher-timeframe context does not align, but that conflict lowers confidence. Under adaptive target management, an aligned context plus displacement or a structure shift supports partial profit-taking with a runner; otherwise the plan remains fixed-target.

### 3.2 Supply/Demand Pullback

This playbook looks for continuation or reaction when price returns to a previously identified supply or demand zone.

Its decision sequence is:

1. Select the nearest permitted zone, prioritising context affinity, quality, and recency.
2. Reject illiquid or unsafe conditions supplied in the evidence.
3. If trend alignment is required, reject explicit opposition; neutral or range context can remain eligible.
4. Require adequate zone quality and a usable lifecycle state.
5. Limit prior distinct touches so repeatedly tested zones do not receive the same weight as fresh zones.
6. Reject excessive penetration or an already invalidated zone.
7. Require price to be reacting from, or still validly inside, the zone.
8. Require a recent trigger in the zone-implied direction.
9. Apply the configured confirmation rules.

Demand zones produce long hypotheses and supply zones produce short hypotheses. The stop sits beyond the far edge of the zone plus an ATR buffer.

With adaptive targets, an aligned context can justify a partial-and-runner plan. Neutral or ranging context keeps the more conservative fixed-target policy.

### 3.3 Liquidity Break and Retest

This is a continuation playbook. It requires evidence that price has genuinely accepted beyond a liquidity level rather than merely spiking through it.

Its decision sequence is:

1. Identify a recent accepted break of a pre-existing liquidity pool.
2. Require sufficient pool quality, unless repeated touches meet the configured exemption.
3. Require a fresh accepted break with meaningful displacement.
4. Reject a recorded break failure.
5. Require price to retest near the broken level.
6. Confirm that the retest held on the accepted side of the level.
7. Require a recent lower-timeframe continuation trigger.
8. Assess CCI and expansion evidence.

Expansion evidence can come from directional ADX/DMI or from sufficient price efficiency combined with volatility expansion or a trending context. By default this evidence adjusts confidence rather than acting as an absolute veto.

Under adaptive management, conflicting context invalidates the expansion hypothesis. Aligned context with expansion evidence produces a managed-expansion plan; aligned context without it produces a partial-and-runner plan.

### 3.4 Indicator Confluence

This optional playbook uses ADX/DMI, RSI, Bollinger, and ATR geometry without requiring a liquidity pool or supply/demand zone. It checks trend direction, momentum that is not already excessively stretched, and volatility conditions. Its stop and target use fixed ATR multiples.

It is disabled by default because its hypothesis is materially different from the structurally anchored playbooks. If enabled, results should be reported separately so performance is not incorrectly attributed to liquidity or zone logic. It currently uses a fixed target rather than the structural adaptive target map.

## 4. Mandatory gates, confidence, and lifecycle

Each playbook is implemented as a sequence of mandatory gates. Examples include catalyst validity, freshness, direction, trigger timing, structural invalidation, and stop/target feasibility. A failed mandatory gate means there is no trade candidate, regardless of the other scores.

For a candidate that passes every mandatory gate, confidence is calculated from:

- the weakest mandatory-gate quality;
- a bounded higher-timeframe context adjustment;
- a bounded confirmation adjustment; and
- a small bonus when multiple playbooks independently support the same direction.

The result is clamped to a 0–100 scale. The normal candidate threshold is 55, and the default minimum trigger confidence is 50.

This confidence is a **rule score**, not a calibrated forecast that a trade has a stated percentage chance of winning. Optional calibration and meta-model policies exist downstream and must not be confused with the playbook score.

### Confirmation modes

Indicators such as CCI can be configured in three modes:

- `Disabled`: ignored;
- `Soft`: agreement raises confidence and conflict lowers it, but the indicator does not veto the setup; or
- `Required`: the setup cannot proceed without the required alignment.

This allows the trading hypothesis to distinguish between structural evidence that defines the setup and an indicator that merely confirms it.

### Playbook lifecycle

The agent tracks each setup through a simple state machine:

```text
Dormant → Armed → CatalystObserved → AwaitingTrigger → CandidateProduced
                                      ↘ Invalidated / Expired
```

Default freshness limits include 12 trigger bars for a trigger opportunity and 24 bars for an armed setup. These limits prevent an old market event from continuing to generate nominally “new” trades after its original rationale has gone stale.

## 5. Entry, stop, and target construction

The agent does not accept a direction alone as a valid trade. It must be able to construct coherent trade geometry.

### Entry reference

Orders are market orders. The estimated execution entry is the latest close adjusted by half the executable spread in the trade direction. The actual fill can differ because of slippage, gaps, latency, or market depth.

### Protective stop

The raw invalidation level comes from the playbook: beyond the sweep, beyond the zone, or beyond the relevant broken structure. The geometry builder adds a default buffer of 0.20 ATR.

The stop must:

- be on the protective side of the entry;
- leave positive price risk; and
- remain within the default maximum distance of 4 ATR.

Every accepted trade carries a protective stop.

### Fixed structural target — version 1

The fixed target process is:

1. Collect structural obstacles ahead of the entry: confirmed swings, valid opposing liquidity pools, and opposing supply/demand zones.
2. Ignore obstacles closer than 0.15 ATR, where the expected move is too small to be useful.
3. Select the nearest valid obstacle.
4. place the target 0.10 ATR before that obstacle to improve the chance of execution ahead of the exact level.
5. Reject the trade if reward is not positive or the reward-to-risk ratio is below the default minimum of 1.5.

Geometry quality rewards excess reward-to-risk and penalises a large spread relative to ATR.

### Adaptive target map — version 2

Adaptive target management builds a ranked map of potential destinations across all configured timeframes. Candidate levels can include confirmed swings, active opposing liquidity pools, and opposing supply/demand zones.

The process:

1. Exclude the catalyst itself and any level on the unprofitable side of the entry.
2. Remove levels that are too close to the entry.
3. Cluster nearby levels within the default 0.25 ATR radius to avoid treating the same market area as several independent targets.
4. Rank candidates by structural tier, distance, quality, prominence, freshness, and confluence.
5. Retain up to eight candidates.
6. Select eligible checkpoints and a terminal objective according to the playbook’s exit policy.

The available policies are:

| Policy | Intended use | Default requirements |
|---|---|---|
| Fixed | Conservative or less supportive context | Terminal target at least 1.5R; no planned partial |
| Partial then runner | Strong setup with scope for continuation | Terminal at least 2R; 25% planned partial and at least 50% retained as runner |
| Managed expansion | Break/retest with expansion evidence | Structural terminal if available, otherwise a synthetic 2R projection |

`R` is the original entry-to-stop price risk. Checkpoints normally require at least 1.25R. If a partial-and-runner setup has no natural checkpoint, the system can create a synthetic checkpoint at 1.5R. Its default 25%/75% weighted plan must still meet 1.5R.

The target planner’s candidate-R calculation is deliberately approximate. Exact commissions, financing, currency conversion, and realised execution costs are handled elsewhere and can cause cash results to differ from the displayed price-based R multiple.

Adaptive plans retain the protective stop but normally suppress the broker take-profit so the position manager can act on checkpoints and changing structure. This makes reliable position-management operation a requirement. Fixed plans retain an explicit take-profit.

The current strategy version guards these two behaviours: adaptive target management is not permitted under the version-1 identity because it changes execution semantics.

## 6. Resolving competing playbooks

Every enabled playbook sees the same evidence packet. The arbitrator then considers only candidates that are both ready and geometrically valid.

The outcomes are:

- no valid candidate: emit `Observe` using the most relevant lifecycle and reason;
- valid candidates in opposing directions: emit `Observe` with `StructuralPlaybookConflict`; or
- one direction only: rank and select the strongest candidate.

Same-direction candidates are ranked deterministically by mandatory-gate quality, geometry quality, confidence, recency, and stable playbook identifier. Multiple independent candidates in the selected direction add a small default confidence bonus of 3 points.

The agent does not average a long and short signal into a weaker trade. Directional disagreement is treated as information uncertainty and therefore as a reason not to enter.

## 7. The trade proposal

If a candidate survives arbitration, the agent emits a proposal containing:

- buy or sell direction;
- market-order intent;
- configured requested quantity;
- reference entry, protective stop, and target or adaptive target plan;
- expected reward-to-risk;
- confidence and component scores;
- the playbook, zone, pool, sweep, and trigger evidence used;
- setup identity and lifecycle state; and
- stable decision and client-order identifiers.

Stable identifiers make retries idempotent: the same logical setup should not become several orders merely because a message or evaluation was repeated.

An `Observe` result is also a formal decision. It includes the most relevant playbook state and reason, such as incomplete analysis, stale evaluation, existing position, expired catalyst, failed gate, inadequate geometry, or playbook conflict.

## 8. Controls after the agent proposes a trade

A strategy proposal is not an execution instruction. The safe trading pipeline can apply the following configured layers before submission:

1. **Data quality and safety:** reject critical or incomplete market data and, where configured, trip a safety control.
2. **Trading conditions:** allow, delay, reduce, or reject based on items such as spread relative to ATR, regime, and data timeliness.
3. **Setup calibration:** optionally reject or reduce historically weaker setup classes.
4. **Meta-model policy:** optionally reject or reduce using an approved secondary model.
5. **Equity protection:** reduce or stop new risk during adverse account conditions.
6. **Position sizing:** convert the proposal into a quantity permitted by the configured sizing method.
7. **Portfolio allocation and reservation:** enforce aggregate risk, concentration, currency, margin, and position-count limits.
8. **Final pre-trade and broker checks:** validate the final order, duplicate status, protective exits, and broker execution state.

Optional filters depend on the resolved deployment profile and should not be described as intrinsic structural-agent rules unless that profile enables them.

### Position sizing

Supported sizing approaches include:

- fixed quantity;
- fixed cash risk; and
- fixed fractional equity risk.

Fixed-quantity mode begins with the strategy’s requested quantity. Risk-based modes use the distance from entry to stop, instrument conversion and contract details, estimated costs, available equity and margin, and current open risk. The final quantity is rounded down to the instrument’s permitted increment.

Configured multipliers can reflect drawdown, volatility, regime, liquidity, correlation, strategy allocation, equity protection, setup calibration, and other approved policies. Their combined purpose is to cap, shrink, or reject a request. They do not leverage a proposal above the strategy request.

The repository’s simulator recommendation uses fixed-fractional sizing at 0.25% of equity, but this is a profile default rather than a universal promise. The exact sizing policy used in a report must be taken from that run’s resolved package.

### Portfolio risk

The central portfolio layer reserves risk before execution so simultaneous strategies cannot each assume the same remaining capacity. Repository defaults include limits for:

- total open risk;
- pending risk;
- risk by strategy and instrument;
- stop risk and net/gross exposure by currency;
- aggregate and single-position margin; and
- maximum open positions.

Representative default ceilings are 1.5% total risk, 0.75% pending risk, 0.75% per strategy, 0.75% per instrument, 30% aggregate margin, 10% single-position margin, and three positions. These values are configurable and must be verified against the deployed profile.

If the portfolio manager has issued an authoritative allocation and reservation, execution uses that reserved quantity rather than sizing the trade again independently.

### Final pre-trade checks

The final risk gate validates, among other things:

- global safety state;
- minimum permitted confidence;
- maximum positions and no-pyramiding rules;
- quantity and account-risk limits;
- valid entry and protective stop;
- take-profit requirements appropriate to the exit mode;
- minimum reward-to-risk for fixed brackets; and
- resulting portfolio heat.

The strategy’s managed-exit mode requires a stop and deliberately permits no take-profit. Its fixed bracket mode requires both stop and target. This prevents the adaptive plan from being rejected merely because it is intended to be managed after entry.

## 9. Management of an open position

At entry, the system pins the original entry, initial stop, structural zone or pool, target policy, and target map. The original `R` therefore remains stable for later profit-protection decisions even if the live stop moves.

The position manager produces recommendations; the runtime execution layer performs the actual stop modification, reduction, or close. Its priorities are broadly:

1. **Account equity protection:** flatten or reduce when the account-level policy requires it.
2. **Optional specialist invalidation:** for example, an enabled NeoWave invalidation policy.
3. **Entry-thesis invalidation:** exit if the entry supply/demand zone is causally invalidated, or an adverse accepted break invalidates the pinned liquidity thesis.
4. **Optional hard market-structure exit:** only when enabled by the profile.
5. **Profit protection:** apply R-based floors and maximum-giveback rules, preferring a tighter stop before a hard exit when feasible.
6. **Risk reduction:** respond to structural deterioration, momentum decay, volatility exhaustion, regime degradation, risk-window or spread stress, planned checkpoints, and stagnation according to the enabled profile.

### Stop management

The manager can construct a tighter stop from:

- break-even adjusted for costs and an ATR allowance;
- current market structure;
- an ATR trail; and
- the profit floor required by the current R multiple.

A stop is only moved in the protective direction. It must improve by the configured minimum amount and remain placeable relative to the executable market price.

Representative structural-management defaults include:

| Profit state | Default protected floor |
|---:|---:|
| 1.00R | 0.00R |
| 1.75R | 0.50R |
| 2.50R | 1.25R |

After 2R, the default maximum permitted giveback is 0.75R; after 3R it is 0.50R. These are management defaults and can be overridden by the active profile.

### Reductions and scale-outs

For a fixed/version-1 position, representative generic scale-outs are 15% at 1.5R and another 15% at 2R or at qualifying opposing structure, while preserving at least a 50% runner.

For an adaptive position, the pinned target-plan checkpoint replaces the generic scale-out so the same profit event is not counted twice. Other risk reductions—such as confirmed structural deterioration or prolonged stagnation—can still apply, subject to the runner floor and the profile’s one-time or cumulative limits.

Representative default reductions include:

- 15% for structural deterioration;
- 10% for momentum decay;
- 10% for volatility exhaustion;
- 20% for regime degradation; and
- 10% for stagnation after 14 bars when the trade has reached at least 1R.

Risk-window stress, spread stress, adverse-structure hard exits, and some specialist exits are disabled in the structural defaults unless explicitly enabled by a profile.

## 10. Default strategy configuration

The most relevant strategy defaults are summarised below. They are starting values, not immutable risk commitments.

| Setting | Default |
|---|---:|
| Context / setup / trigger | 1h / 15m / 5m |
| Requested quantity | 1,000 units |
| Minimum reward-to-risk | 1.5 |
| Stop buffer | 0.20 ATR |
| Target buffer | 0.10 ATR |
| Maximum trigger age | 12 trigger bars |
| Maximum armed age | 24 bars |
| Maximum stop distance | 4 ATR |
| Minimum obstacle distance | 0.15 ATR |
| Adaptive target management | Off |
| Strategy identity | `structural-confluence-v1` |

The option names `StrongOppositionVeto` and `MinimumContextConfidence` are currently validated configuration fields but are not applied as independent hard gates by the active structural playbooks. `AllowRangeBoundaryContext` is likewise not a separate active decision branch in the current pullback logic. Reports should describe actual behaviour rather than infer behaviour from these names alone.

## 11. Auditability and reproducibility

The design includes several features intended to support review:

- deterministic playbook ordering and tie-breaking;
- one common, point-in-time evidence packet;
- stable setup, decision, and client-order identities;
- explicit `Observe` reason codes;
- playbook lifecycle and gate scores;
- recorded zone, pool, sweep, trigger, stop, target, and policy evidence;
- immutable resolved policy packages with configuration hashes and source-commit identity; and
- separate records for strategy intent, risk allocation, broker submission, and position-management action.

For a finance or model-governance report, retain at least:

- strategy version and source commit;
- exact resolved options and policy hashes;
- instrument and evaluation time;
- all source-analysis versions and `AvailableAt` times;
- selected playbook and rejected alternatives;
- gate qualities, confidence adjustments, and arbitration result;
- expected versus filled entry, spread, slippage, stop, and target plan;
- requested, risk-approved, and filled quantities;
- reservation and rejection reasons;
- every stop, reduction, and exit recommendation and execution; and
- realised gross and net P&L in both cash and R.

## 12. Important limitations and review points

- **No performance guarantee:** a coherent structural setup can still lose. This document describes process, not expected return.
- **Technical scope:** the strategy playbooks do not natively assess earnings, central-bank decisions, macro releases, or news shocks.
- **Upstream dependency:** setup quality depends on the correctness and timeliness of the structure, zone, liquidity, and indicator analysers.
- **Confidence interpretation:** the score ranks rule quality; it is not a calibrated win probability.
- **Execution gap:** proposed entry and R are price-based estimates. Slippage, spread changes, gaps, commission, financing, and currency conversion affect realised results.
- **State recovery:** lifecycle and evaluation-epoch state are held in memory and require care across restarts or failover.
- **Adaptive-operation dependency:** adaptive trades have no broker take-profit and therefore depend on the position-management loop; the protective stop remains the last-resort order protection.
- **Indicator-playbook comparability:** the optional indicator playbook is not structurally anchored and should be evaluated as a separate strategy cohort.
- **Profile sensitivity:** risk limits, sizing, optional filters, management thresholds, and even enabled playbooks can differ by environment.
- **Unused-looking controls:** some validated context options are not currently consumed as distinct hard gates, as noted above.
- **Deployment governance:** the repository catalog does not automatically certify this agent for unattended demo or live trading. Supported operating modes include observe-only, shadow, manual approval, and automatic, but automatic use requires the relevant external approval and profile controls.
- **Backtest realism:** historical results must preserve the availability-time rules and use credible assumptions for intrabar ordering, spread, slippage, gaps, and partial fills.

## 13. Suggested finance review framework

Performance should be segmented by the decision variables that materially change the hypothesis:

- playbook and direction;
- instrument, session, and volatility regime;
- context aligned, neutral, or opposed;
- fixed, partial-and-runner, or managed-expansion target policy;
- confidence decile and weakest-gate quality;
- planned versus realised R and implementation shortfall;
- proposed versus approved quantity and the policy that reduced it;
- `Observe` and rejection reason frequency;
- maximum adverse and favourable excursion;
- stop, target, thesis-invalidation, and management-exit outcomes; and
- live/shadow results versus the same-version backtest.

This separation helps distinguish four different questions: whether the setup logic has edge, whether execution preserves it, whether sizing and portfolio controls contain losses, and whether post-entry management adds or removes value.

## Glossary

| Term | Meaning in this system |
|---|---|
| ATR | Average True Range, used to normalise distances and buffers across volatility regimes |
| Catalyst | The structural event that activates a setup, such as a sweep, zone interaction, or accepted break |
| Confirmation | Supporting evidence that adjusts or, when configured, gates a structurally defined setup |
| Liquidity pool | A price area where resting orders are expected around a structural level |
| Accepted break | A move through a level that shows continuation/acceptance rather than immediate rejection |
| Sweep | A temporary move through liquidity followed by rejection back through the level |
| R | Original entry-to-initial-stop price risk; 2R is twice that distance in favourable movement |
| Runner | The portion intentionally left open after a partial reduction |
| Geometry | The relationship between proposed entry, invalidation stop, obstacles, targets, spread, and reward-to-risk |
| `Observe` | A completed decision not to request a new trade at that evaluation point |

## Implementation map and related material

The main implementation files are deliberately separated by responsibility:

- [`StructuralConfluenceAgent.cs`](StructuralConfluenceAgent.cs) — evaluation, idempotency, playbook coordination, and decision output.
- [`Evidence/StructuralEvidencePacketFactory.cs`](Evidence/StructuralEvidencePacketFactory.cs) — point-in-time evidence construction.
- [`Playbooks/`](Playbooks/) — playbook rules and lifecycle state.
- [`StructuralCandidateArbitrator.cs`](StructuralCandidateArbitrator.cs) — conflict handling and deterministic selection.
- [`StructuralGeometryBuilder.cs`](StructuralGeometryBuilder.cs) — version-1 entry, stop, obstacle, and target geometry.
- [`TargetManagement/TargetMapBuilder.cs`](TargetManagement/TargetMapBuilder.cs) — version-2 adaptive target map.
- [`StructuralConfluenceStrategyOptions.cs`](StructuralConfluenceStrategyOptions.cs) — strategy defaults and validation.

Repository-level design and implementation history:

- [`../../../TradingHub_Structural_Confluence_Strategy_Logic.md`](../../../TradingHub_Structural_Confluence_Strategy_Logic.md)
- [`../../../TradingHub_Structural_Agent_Improvement_Final_Report.md`](../../../TradingHub_Structural_Agent_Improvement_Final_Report.md)
- [`../../../TradingHub_Structural_Indicator_Target_Management_Implementation_Plan.md`](../../../TradingHub_Structural_Indicator_Target_Management_Implementation_Plan.md)

Those documents provide useful design background, but this README should be used as the current high-level description when historical proposals differ from the implemented code.
