# TradingHub Structural Indicator and Adaptive Target Management Implementation Plan

  ## 1. Purpose and outcome

  This plan extends the existing structural-confluence implementation. It does not replace the three playbooks, deterministic evidence pipeline, content-hash validation, or causality rules.

  The implementation has two connected goals:

  1. Increase qualified structural entries by preventing a nearby weak swing from incorrectly becoming the final target and causing an unnecessary low-R:R rejection.
  2. Keep profitable trend and breakout trades running after the first target is reached, while protecting accumulated profit through partial exits, structural trailing stops, profit floors, and controlled
     giveback.

  A target touch will not automatically close a trend trade. Only range/countertrend setups using a fixed-target policy will close the complete position at their target.

  ### Existing behavior being corrected

  The current StructuralGeometryBuilder:

  - Enumerates swings, liquidity pools, and opposing supply/demand zones.
  - Sorts all candidates only by distance.
  - Selects the nearest candidate as the final target.
  - Rejects the setup if that target is below MinimumRewardRisk, currently 1.5R.
  - Submits the target as a hard bracket take-profit.

  This causes two problems:

  - A weak internal swing can reject a setup even when a stronger external objective is available.
  - A valid breakout can close completely at the nearest target even when price accepts the breakout and continues trending.

  ### Explicit non-goals

  - Do not replace S&D or Liquidity as the primary trade disciplines.
  - Do not make RSI, CCI, Bollinger Bands, or moving averages independent entry generators.
  - Do not add new playbooks in this phase.
  - Do not rewrite stored historical profile or experiment JSON.
  - Do not bypass content-hash verification.
  - Do not enable live certification automatically.
  - Do not promise a particular number of trades or financial return.
  - Do not use future candles or objects whose AvailableAt is later than the decision time.

  ———

  ## 2. Locked strategy behavior

  ### 2.1 Structural evidence remains primary

  The entry hierarchy remains:

  1. Context and market regime.
  2. Structural location: S&D zone or liquidity pool.
  3. Structural catalyst: sweep, accepted break/retest, or zone reaction.
  4. Completed-candle price-action trigger.
  5. Indicator confirmation.
  6. Stop geometry, target map, costs, and R:R admission.
  7. Arbitration between ready playbooks.

  Indicators may:

  - Increase or decrease confidence.
  - Confirm a structural catalyst.
  - Veto strong contradictory conditions.
  - Select the appropriate exit policy.

  Indicators may not:

  - Create a trade without a valid structural location and catalyst.
  - Override an invalidated zone or consumed liquidity event.
  - Override an invalid stop.
  - Override an important hard barrier before the minimum acceptable reward.

  ### 2.2 Multi-timeframe indicator interpretation

  Continue using configured structural intervals. Current defaults remain:

  - Context: 1h
  - Setup: 15m
  - Trigger: 5m

  Do not assume RSI must always increase when moving to a lower timeframe. For example, RSI 70 on 5-minute, 80 on 1-minute, and 90 on 1-second can occur, but it is not a reliable breakout rule by itself. Each
  timeframe measures momentum over a different candle series.

  Evaluate each indicator on its own completed timeframe:

  - Context timeframe: trend and regime.
  - Setup timeframe: location and setup momentum.
  - Trigger timeframe: entry timing and immediate confirmation.

  Only consume indicator relationships whose confirmation timestamp is available at the current decision time.

  ### 2.3 Moving-average regime

  Use existing Sma50 and Sma200 fields.

  Classify each relevant timeframe as:

  - Bullish: close is above SMA50 and SMA50 is above SMA200.
  - Bearish: close is below SMA50 and SMA50 is below SMA200.
  - Mixed: any other ordering.
  - Unavailable: one or both averages are missing.

  Rules:

  - Matching context and setup alignment supports continuation and breakout policies.
  - Mixed alignment is neutral; it does not automatically reject a trade.
  - Opposite alignment on both context and setup, combined with an opposing structural market regime, is a strong opposition veto.
  - Missing averages are neutral unless a profile explicitly configures them as required.

  ### 2.4 RSI and Bollinger Bands

  Reuse the existing RSI relationship and RsiBollingerSignalPolicy implementation instead of creating another RSI engine.

  Use RSI for:

  - Regular divergence as evidence for reversal setups.
  - Hidden divergence or convergence as evidence for continuation.
  - Momentum direction after a zone reaction.
  - Strong opposing divergence as a configurable veto.

  Use Bollinger analysis for:

  - Squeeze release and bandwidth expansion during breakouts.
  - Re-entry after a temporary band excursion during pullbacks.
  - Detecting volatility expansion versus contraction.
  - Confirming that momentum has accepted a breakout rather than only wicked through a level.

  An overbought or oversold value alone must not trigger or veto a trade.

  ### 2.5 CCI

  Reuse the existing CCI snapshot, relationship, cross, momentum, and extreme-state data.

  Playbook interpretation:

  - Liquidity sweep reversal:
      - Regular divergence, a cross back from an extreme, or aligned momentum supports the reversal.

  - S&D pullback:
      - Hidden divergence, convergence, aligned momentum, or a zero-line recovery supports continuation.

  - Liquidity break/retest:
      - Aligned momentum, convergence, or a directional zero-line cross supports breakout continuation.

  CCI remains configurable as Disabled, Soft, or Required.

  If a required confirmation is unavailable, reject with an explicit reason. If it is configured as soft, unavailable data contributes zero adjustment and must not silently reject the setup.

  ### 2.6 Optional learning failures

  The adaptive target system must not depend on successful model training.

  - Optional learned output failure: use the deterministic structural baseline, apply no learned adjustment, and record a degraded-learning diagnostic.
  - Required learned output failure: fail closed with a specific reason code.
  - Never silently reuse stale or partially generated learned output.
  - Learning errors must be visible separately from structural and indicator rejection reasons.

  ———

  ## 3. Target-map and R:R design

  ### 3.1 Replace nearest-target selection with a target map

  Build an ordered target map from causal candidates on trigger, setup, context, and configured additional-context intervals.

  Candidate sources:

  - Confirmed swing highs and lows.
  - Active liquidity pools.
  - Active opposing S&D zones.
  - Previous session/day/week levels already represented by liquidity pools.
  - A synthetic R-multiple projection only as a managed-expansion fallback.

  Eligible liquidity states:

  - Active
  - Approached
  - Touched

  Exclude:

  - Forming
  - Swept
  - AcceptedBreak
  - Consumed
  - Broken
  - Expired
  - Merged

  Eligible S&D states:

  - ConfirmedFresh
  - Approached
  - Tested
  - PartiallyMitigated

  Exclude:

  - Forming
  - Mitigated
  - Invalidated
  - Expired
  - Merged

  Every candidate must:

  - Be on the profitable side of the entry.
  - Be at least 0.15 ATR from the entry.
  - Have been confirmed and available by the decision timestamp.
  - Preserve its source object ID, interval, state, bounds, quality, and reason codes.

  The level broken by the entry catalyst must not be reused as the final target. After an accepted breakout, that level becomes structural support/resistance for stop management.

  ### 3.2 Candidate roles

  Every candidate receives one role:

  - Checkpoint: an internal objective suitable for a partial exit.
  - Terminal: a meaningful external objective.
  - HardBarrier: a strong opposing structure that limits realistic opportunity.
  - Projection: a synthetic managed-expansion reference, never a hard broker target.

  ### 3.3 Candidate ranking

  Use deterministic tiers before considering distance.

  #### Tier A: terminal or hard-barrier candidates

  - Opposing context/setup S&D zone with quality at least 0.55, acceptable lifecycle, and no more than one prior touch.
  - Context/setup liquidity pool with quality at least 0.65 and prominence at least 0.60.
  - Previous session/day/week liquidity references meeting the same lifecycle requirements.
  - A cluster containing at least two independent sources or intervals within 0.25 ATR.

  #### Tier B: terminal candidates

  - A setup-timeframe active liquidity pool with sufficient quality but lower prominence.
  - A setup-timeframe confirmed swing that is reinforced by another candidate within 0.25 ATR.
  - A high-quality trigger-timeframe S&D zone or liquidity pool reinforced by setup structure.

  #### Tier C: checkpoints and soft obstacles

  - Isolated trigger/setup swings.
  - Weak or internal liquidity.
  - Single-timeframe structure without supporting confluence.

  Tier C candidates do not reject a trade solely because they are below 1.5R.

  Within each tier, rank by:

  1. Higher timeframe.
  2. Quality.
  3. Prominence or structural strength.
  4. Freshness.
  5. Lower prior-touch count.
  6. Distance.
  7. Stable source identity as the final deterministic tie-breaker.

  Persist at most the first eight relevant candidates after ranking.

  ### 3.4 Candidate clustering

  Group candidate execution prices within 0.25 ATR.

  The cluster identity is a deterministic hash of sorted source identities. Preserve every constituent source in the audit data.

  A cluster with independent structural sources is promoted by one significance tier. Multiple representations of the same source lineage do not count as independent confluence.

  ### 3.5 Target execution prices

  For long trades:

  - Liquidity target: lower edge of the pool minus the existing 0.10 ATR target buffer.
  - Supply target: nearest edge of the supply zone minus the buffer.
  - Swing target: swing price minus the buffer.

  For short trades, use the mirrored upper edge plus the buffer.

  Persist both the original candidate boundaries and the buffered execution price.

  ### 3.6 Reachability and barriers

  Evaluate candidates outward from entry.

  - A Tier C obstacle may become a checkpoint but does not block a farther target.
  - A hard barrier before 1.5R rejects the setup.
  - A terminal target cannot be selected beyond an unbroken hard barrier.
  - After a hard barrier is causally accepted and consumed, rebuild the target map and allow a farther objective.
  - Do not move a target farther merely because price touched it.

  ### 3.7 Cost-adjusted R

  Pin 1R when the trade opens. It never changes later, even if the stop is trailed.

  Definitions:

  InitialRiskCash = gross stop-loss amount + entry costs + estimated stop-exit costs

  TargetRewardCash = gross target profit - entry costs - estimated target-exit costs

  TargetR = TargetRewardCash / InitialRiskCash

  PlannedR = Σ(exit fraction × objective R)

  RealizedR = total net trade P&L / original InitialRiskCash

  Costs include the configured spread, commission, slippage, and instrument value conversion.

  If monetary conversion is unavailable at decision time:

  - Use the conservative configured per-unit cost estimate for admission.
  - Resolve and persist the final cash risk when the position opens.
  - Never recalculate the original R denominator after entry.

  Example:

  - Exit 25% at 1.5R.
  - Manage the remaining 75% toward 3R.

  PlannedR = 0.25 × 1.5 + 0.75 × 3 = 2.625R

  ### 3.8 Admission gates

  #### Fixed structural target

  - Terminal target must be at least 1.5R.
  - No hard barrier may invalidate the route.
  - A hard take-profit is submitted.

  #### Partial then runner

  - Conservative terminal opportunity must be at least 2R.
  - Weighted planned reward must be at least 1.5R.
  - No hard barrier may occur before 1.5R.
  - No hard take-profit is submitted.

  #### Managed expansion

  - Expansion projection must reach at least 2R.
  - No hard barrier may occur before 1.5R.
  - Breakout acceptance, directionally aligned context, and expansion evidence are mandatory.
  - No hard take-profit is submitted.

  A synthetic 2R projection may be used only for ManagedExpansion when no external terminal candidate exists and expansion conditions are satisfied. It is an opportunity measurement and report reference, not an
  order.

  ———

  ## 4. Exit-policy routing and management

  ### 4.1 Exit policies

  Add:

  - FixedStructuralTarget
  - PartialThenRunner
  - ManagedExpansion

  Routing is deterministic.

  #### S&D pullback

  - Trend-aligned S&D pullback: PartialThenRunner.
  - Range-boundary S&D reversal: FixedStructuralTarget.
  - S&D setup opposed to an established context trend: reject.
  - Indicators may confirm a range or trend classification but cannot override the structural opposition veto.

  #### Liquidity sweep reversal

  - Range or countertrend sweep: FixedStructuralTarget.
  - Sweep aligned with context after displacement, reclaim, and structure shift: PartialThenRunner.
  - A sweep without adequate reclaim/trigger confirmation remains rejected.

  #### Liquidity break/retest

  - Accepted break with aligned context and volatility expansion: ManagedExpansion.
  - Accepted break with aligned context but neutral expansion: PartialThenRunner.
  - Conflicting context or failed acceptance: reject.

  Expansion evidence is satisfied by the existing break/retest structural gates plus at least one of:

  - Bollinger squeeze release or expansion.
  - ATR expansion.
  - Efficient directional regime with acceptable ADX/efficiency evidence.

  ### 4.2 Fixed-target behavior

  For FixedStructuralTarget:

  - Submit the protective stop and hard take-profit.
  - Close 100% at the target.
  - Preserve the bracket target.
  - Do not dynamically move the target.
  - Normal stop, cancellation, risk, and session rules remain active.

  ### 4.3 Trend-policy behavior

  For PartialThenRunner and ManagedExpansion:

  - Submit a mandatory protective stop.
  - Do not submit a hard take-profit.
  - A simple touch of a checkpoint or terminal candidate never closes the complete position.
  - The manager controls partial exits, trailing stops, target advancement, and eventual closure.
  - Entry must be rejected if strategy-exit management is unavailable because a managed position must not open without its manager.

  ### 4.4 Partial exits

  Use one target-aware partial exit instead of the existing two generic 15% structural scale-outs.

  PartialThenRunner:

  - Select the first meaningful structural checkpoint at or beyond 1.25R.
  - If none exists, use a synthetic 1.5R partial checkpoint.
  - Close 25% of the original quantity once.

  ManagedExpansion:

  - Use the first meaningful structural checkpoint at or beyond 1.25R.
  - Do not create a synthetic early partial.
  - Close 25% once when that structural checkpoint is reached.

  Rules:

  - Never perform the same checkpoint partial more than once.
  - Persist processed checkpoint identities for restart recovery.
  - Retain at least 50% of original quantity as the runner.
  - The default result after one partial is a 75% runner.
  - Disable generic Structural scale-out rules for the new adaptive policy to prevent duplicate reductions.
  - Keep generic scale-outs available for legacy profiles and other agent types.

  ### 4.5 Target acceptance

  A target is accepted and may be advanced when either:

  - Its lifecycle reports a causal accepted-break/consumption event, or
  - A completed trigger candle:
      - Closes at least 0.10 ATR beyond the far boundary.
      - Has a body of at least 0.35 ATR.
      - Has a body-to-range ratio of at least 0.50.
      - Is directionally aligned.
      - Is not contradicted by a confirmed context reversal.

  On acceptance:

  - Mark the target consumed in the trade’s logical target plan.
  - Rebuild the outward target map using only currently available evidence.
  - Record a target-plan revision and its cause.
  - Ratchet the stop using newly protected structure.
  - Continue holding the runner.

  ### 4.6 Target rejection

  A simple target touch is not rejection.

  Strong rejection requires:

  - Penetration or touch of the target area.
  - A completed close back through the entry-side boundary.
  - A directionally opposing price-action event or strong opposing RSI/CCI relationship.
  - No accepted-break state.

  For a strong terminal rejection:

  - Close the remaining position.

  For weaker deterioration:

  - Allow the existing manager reduction, profit-floor, and trailing logic to decide.
  - Do not advance the target.

  ### 4.7 Trailing stop

  Activate trend trailing after the first of:

  - The trade reaches 1R.
  - An accepted retest completes.
  - A checkpoint is consumed with directional displacement.

  Preferred structural trail:

  - Long: latest confirmed and protected swing low after entry, minus 0.25 ATR.
  - Short: latest confirmed and protected swing high after entry, plus 0.25 ATR.
  - A swing is protected only after confirmation and subsequent favorable structure continuation.

  Fallback:

  - Long: highest price since entry minus 2.5 ATR.
  - Short: lowest price since entry plus 2.5 ATR.

  Constraints:

  - Stop can only move toward profit.
  - Never widen risk.
  - Do not place the stop through the current executable bid/ask.
  - Keep the existing allowable trailing-distance range of 0.35–2.5 ATR.
  - At 1R, move to cost-adjusted break-even when execution geometry permits it.
  - Existing profit floors remain:
      - 1R MFE → minimum 0R
      - 1.75R MFE → minimum 0.5R
      - 2.5R MFE → minimum 1.25R

  - Existing giveback controls remain:
      - Activate at 2R MFE.
      - Maximum normal giveback: 0.75R.
      - From 3R MFE: maximum giveback 0.50R.

  ### 4.8 Full-exit conditions for runners

  Close the remaining position on:

  - Protective stop.
  - Strong terminal rejection.
  - Structural setup invalidation.
  - Confirmed context regime reversal.
  - Existing risk/session/cancellation rules.
  - Existing profit-floor or MFE-giveback exit.
  - Manager failure requiring a safe fail-closed liquidation.

  Do not close the full runner solely because the initial target was touched or because the original planned R was reached.

  ### 4.9 Event ordering

  Use the existing deterministic intrabar execution policy.

  For management decisions on the same completed candle:

  1. Resolve protective stop or broker-level terminal events.
  2. Resolve full structural invalidation or strong rejection.
  3. Apply an eligible partial.
  4. Apply target-plan advancement.
  5. Apply trailing-stop or break-even amendment.

  When a partial and stop amendment occur together, use the existing combined reduction-and-stop action.

  ———

  ## 5. Contracts, persistence, compatibility, and reports

  ### 5.1 Public model additions

  Add the following shared contracts.

  TradeExitPolicy:

  - FixedStructuralTarget
  - PartialThenRunner
  - ManagedExpansion

  TradeTargetRole:

  - Checkpoint
  - Terminal
  - HardBarrier
  - Projection

  TradeTargetSourceKind:

  - Swing
  - LiquidityPool
  - SupplyDemandZone
  - RMultipleProjection

  TradeTargetCandidate contains:

  - Stable candidate/cluster identity.
  - Source kind and source identity.
  - Source interval.
  - Lifecycle state.
  - Original lower/upper boundaries.
  - Buffered execution price.
  - Candidate role and significance tier.
  - Quality, prominence, freshness, and touch data when applicable.
  - Distance in ATR.
  - Cost-adjusted target R.
  - Constituent source identities for clusters.
  - Availability timestamp.
  - Reason codes.

  TradeTargetPlan contains:

  - Plan version and revision number.
  - Exit policy.
  - Original entry and stop.
  - Initial price risk.
  - Selected checkpoint.
  - Selected terminal or projection.
  - Ordered candidate list.
  - Partial fraction.
  - Minimum runner fraction.
  - Planned R.
  - Conservative opportunity R.
  - Creation timestamp.
  - Last revision timestamp and revision reason.

  TargetPlanRevision contains:

  - Previous and new revision.
  - Triggering structural event.
  - Consumed candidate identity.
  - Newly selected checkpoint/terminal.
  - Decision timestamp.

  ### 5.2 Existing model compatibility

  Extend AgentDecision, StructuralGeometry, managed-trade state, simulated trade records, replay records, and report DTOs additively.

  Compatibility fields:

  - TakeProfitPrice:
      - Set only for FixedStructuralTarget.
      - Null for managed trend policies.

  - ExpectedRewardRisk:
      - Continue populating it from TradeTargetPlan.PlannedR.

  - TargetSource:
      - Continue populating it from the selected terminal or projection.

  - StopLossPrice remains mandatory for every structural entry.

  Old records without a target plan must deserialize with legacy behavior.

  ### 5.3 Trade-manager contract

  Add an optional target plan and target-plan revision to managed-trade input/output.

  Logical target advancement must not require a broker take-profit amendment because trend policies do not place a hard target order.

  Persist:

  - Original target plan.
  - Current target plan revision.
  - Processed checkpoint identities.
  - Partial quantities and fills.
  - Stop amendments.
  - Target acceptance/rejection events.
  - Exit-policy reason.
  - Original initial-risk cash.
  - Realized R.

  ### 5.4 Version and content hashes

  Introduce strategy version:

  structural-confluence-v2

  Add an AdaptiveTargetManagement options block. Default Enabled to false for compatibility.

  The new v2 profile explicitly enables it. Existing v1 profiles retain bracket behavior and their current generic scale-outs.

  All new configuration fields participate in canonical configuration hashing.

  Never:

  - Recalculate and overwrite an existing stored revision hash.
  - Mutate historical profile JSON in place.
  - Disable validation to make old data load.
  - Reuse a v1 strategy version for changed decision behavior.

  ### 5.5 Reports and experiment visibility

  Expose in single-run and experiment reports:

  - Exit policy.
  - Original target map.
  - Selected checkpoint and terminal.
  - Every target-plan revision.
  - Planned R and realized R.
  - Original risk cash.
  - Partial exits.
  - Runner quantity.
  - MFE, MAE, and R captured from MFE.
  - Stop amendments.
  - Exit reason.
  - Entry rejection reason, including weak-obstacle bypass versus hard-barrier rejection.
  - Indicator confirmation states.
  - Learning-degraded status.

  Reports must make it possible to determine:

  - Whether a nearby swing was treated as a soft checkpoint instead of a final target.
  - Whether a target touch, rejection, or accepted break occurred.
  - Why the runner remained open or closed.
  - Which experiment, simulation, configuration, profile, and trade IDs produced the result.

  ———

  ## 6. Implementation sequence

  ### Phase 1: contracts and compatibility

  - Add exit-policy, target-candidate, target-plan, and revision contracts.
  - Add AdaptiveTargetManagementOptions with validation.
  - Add nullable target-plan fields to decision, persistence, and report models.
  - Preserve exact v1 behavior when adaptive management is disabled.
  - Add canonical hashing coverage for the new options.

  ### Phase 2: deterministic target-map builder

  - Replace nearest-obstacle selection for v2 with the tiered target-map builder.
  - Implement lifecycle filtering, clustering, ranking, reachability, and barrier rules.
  - Implement cost-adjusted R and planned-R calculations.
  - Preserve the existing structural stop calculation and stop validity gates.
  - Ensure long/short behavior is perfectly mirrored.

  ### Phase 3: playbook integration

  - Route each ready playbook to its deterministic exit policy.
  - Reuse existing RSI, CCI, Bollinger, SMA, regime, ADX, and efficiency evidence.
  - Normalize S&D and liquidity quality consistently:
      - Native zone/pool quality stays in 0–1.
      - Convert to 0–100 exactly once when contributing to confidence.

  - Add explicit indicator, learning, geometry, barrier, and policy reason codes.

  ### Phase 4: simulator and trade manager

  - Submit hard targets only for fixed-target trades.
  - Require manager availability for managed policies.
  - Implement target-aware 25% partials.
  - Disable duplicate generic Structural partials for v2.
  - Implement target acceptance, rejection, logical advancement, structural trail, and fallback trail.
  - Persist state sufficiently to resume without duplicate partials after restart.
  - Keep simulator and live coordinator semantics identical.

  ### Phase 5: experiment and reporting

  Run two comparisons:

  1. Exit-only comparison using identical entries:
      - v1 fixed bracket.
      - v2 adaptive exit management.

  2. Full-funnel comparison:
      - v1 nearest-target geometry.
      - v2 target-map admission and adaptive exits.

  Segment results by:

  - Playbook.
  - Instrument.
  - Session.
  - Context regime.
  - Exit policy.
  - Volatility regime.
  - Long versus short.

  Do not certify live trading as part of implementation.

  ———

  ## 7. Tests and acceptance criteria

  ### 7.1 Unit tests

  Target-map tests:

  - Weak nearest swing becomes a checkpoint while a stronger external target remains terminal.
  - Hard opposing S&D zone before 1.5R rejects.
  - Consumed, broken, swept, invalidated, expired, and future-available objects are excluded.
  - Broken catalyst level cannot become the target.
  - Clustering is deterministic and does not double-count source lineage.
  - Stable tie-breaking produces identical decisions after replay.
  - Long and short cases are symmetric.
  - Projection is allowed only for managed expansion.

  R tests:

  - Spread, commission, slippage, and stop-exit costs are included.
  - Planned R correctly handles partial quantities.
  - Realized R always uses the original risk denominator.
  - Stop amendments do not change 1R.

  Indicator tests:

  - RSI values across timeframes are evaluated independently.
  - Soft unavailable confirmation does not reject.
  - Required unavailable confirmation rejects explicitly.
  - Strong opposing context and moving-average alignment veto correctly.
  - Optional learning failure falls back to deterministic behavior.

  ### 7.2 Trade-management tests

  - Fixed policy submits a hard target and closes completely at it.
  - Managed policies submit no hard target.
  - A target touch alone does not close a runner.
  - A checkpoint closes exactly 25% once.
  - Restart recovery does not repeat a partial.
  - Accepted breakout advances the target plan.
  - Strong rejection closes the remaining position.
  - Structural stop only ratchets toward profit.
  - Chandelier fallback operates when no protected swing exists.
  - Profit floors and MFE giveback remain enforced.
  - Existing generic scale-outs do not duplicate v2 target-aware partials.
  - Manager unavailability prevents a managed entry.
  - Simulation and live decision paths produce equivalent order intent.

  ### 7.3 Compatibility tests

  - Existing v1 profile produces unchanged decisions.
  - Existing profile and experiment hashes remain valid.
  - New v2 settings produce a different deterministic hash.
  - Old JSON records deserialize without target-plan fields.
  - Replay produces identical target maps and revisions.
  - Report clients tolerate additive nullable fields.

  ### 7.4 Experiment metrics

  Primary:

  - Net expectancy after costs in R per trade.
  - Profit factor.
  - Maximum drawdown.
  - Total net R.

  Secondary:

  - Qualified trade count.
  - StructuralRewardRiskBelowMinimum rejection count.
  - Win rate.
  - Average winner and loser in R.
  - Average holding time.
  - Partial-exit contribution.
  - Runner contribution.
  - MFE capture percentage.
  - Premature full-target exit rate.
  - Profit giveback from MFE.
  - Slippage and total transaction costs.

  ### 7.5 Promotion criteria

  Do not make a performance decision before:

  - At least 100 completed v2 trades overall.
  - At least 30 completed trades for every materially affected playbook cohort.
  - A separate held-out market period.

  Candidate acceptance requires:

  - Positive expectancy after costs.
  - Expectancy not worse than v1 by more than 0.05R per trade.
  - Maximum drawdown no greater than 110% of the comparable v1 drawdown.
  - No increase in causality, replay, hash, or state-recovery failures.
  - At least a 10% improvement in winner MFE capture for trend/breakout cohorts.
  - At least a 25% reduction in premature complete exits at subsequently accepted targets.
  - Qualified opportunity count must not decrease without an offsetting statistically meaningful expectancy improvement.

  Live promotion remains a separate, explicitly approved task.

  ———

  ## 8. Planning hypotheses, not guarantees

  For one liquid instrument during London and New York sessions, initial experiment expectations are:

  - Slow day: 0–2 entries.
  - Normal day: 2–5 entries.
  - Volatile day: 4–8 entries.
  - Long-run planning estimate: approximately 2–4 entries per instrument per active day.

  These numbers depend on instrument, spread, session, volatility, profile settings, and data quality. They are not implementation acceptance criteria.

  Approximate winning-trade design ranges:

  - S&D range/countertrend: 1.2–1.8R.
  - Trend-aligned S&D pullback: 1.5–2.5R.
  - Liquidity sweep reversal: 1.3–2.2R.
  - Liquidity break/retest: 1.8–3.5R, with occasional larger runners.

  At 0.5% account risk per trade, those winning ranges correspond approximately to:

  - 1.2R → +0.60%
  - 1.5R → +0.75%
  - 2R → +1.00%
  - 2.5R → +1.25%
  - 3.5R → +1.75%

  These describe individual winners before portfolio-level effects. They do not represent expected daily return. Expectancy must include losses, costs, partial exits, skipped trades, and drawdown.

  ———

  ## 9. Locked assumptions and defaults

  - Existing structural playbooks and evidence pipeline remain.
  - Default intervals remain 1h / 15m / 5m.
  - Existing stop buffer remains 0.20 ATR.
  - Existing target buffer remains 0.10 ATR.
  - Minimum obstacle distance remains 0.15 ATR.
  - Fixed-target minimum remains 1.5R.
  - Managed opportunity minimum is 2R.
  - Minimum weighted planned reward is 1.5R.
  - Target cluster distance is 0.25 ATR.
  - Default partial is 25%.
  - Default runner after the first partial is 75%.
  - Runner must never be reduced below 50% merely by planned scale-outs.
  - Structural trailing buffer is 0.25 ATR.
  - Chandelier fallback is 2.5 ATR.
  - Target recalculation is event-driven, never performed merely on every candle.
  - Trend targets are logical objectives rather than hard broker orders.
  - Protective stops are mandatory for every trade.
  - No complete runner exit occurs merely because a planned target was reached.
  - Old v1 behavior remains available and unchanged.
  - The new behavior is introduced as structural-confluence-v2.
  - Open implementation decisions: none.
