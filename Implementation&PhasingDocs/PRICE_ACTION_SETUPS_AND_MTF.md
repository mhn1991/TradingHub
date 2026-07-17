# Price-action composite setups & multi-timeframe policy

This document describes Phase 1–2 of the price-action upgrade implemented in the codebase.
Use it when revisiting entry logic, debugging why a setup did/did not fire, or extending agents.

## Goals

1. Turn per-bar atomic PA events into **named, stateful composite setups** (same timeframe).
2. Let **higher-timeframe (HTF) context arm/veto** lower-timeframe (LTF) entries without projecting HTF geometry onto LTF candles.
3. Wire progressive agents so **orders carry setup lineage** (type, id, reference level) and prefer setup-based stops.

## Architecture

```
Per TF ChartAnnotationEngine
  PriceActionAnalyzer.Update  → atomic events + ActiveRetest
  PriceActionSetupComposer.Apply → PriceActionSnapshot.Setups[]
                │
                ▼
ProgressiveStrategyBase (entry gate)
  MultiTimeframePriceActionPolicy.EvaluateEntry(trend, entry, mode, …)
                │
                ▼
AgentDecision (+ optional setup id/type/level, stop from setup reference)
```

| Layer | Location | Responsibility |
|---|---|---|
| Atomic PA | `ChartAnnotator/PriceAction/PriceActionAnalyzer.cs` | BOS, CHOCH, retest, rejection, displacement, sweep, BB breakout |
| Composites (Phase 1) | `ChartAnnotator/PriceAction/PriceActionSetupComposer.cs` | Same-TF state machines → `PriceActionSetup` |
| MTF policy (Phase 2) | `Agent/Strategies/MultiTimeframePriceActionPolicy.cs` | HTF arms/vetoes + LTF gate |
| Agent wiring | `Agent/Strategies/ProgressiveStrategyBase.cs`, Improved/Legacy agents | Gate entries; annotate orders; setup-aware stops |

## Phase 1 — Same-timeframe composite setups

### Models (`ChartAnnotator/Models/AnalysisModels.cs`)

- `PriceActionSetupType` — named setup kinds
- `PriceActionSetupPhase` — `Armed | Triggered | Invalidated | Expired`
- `PriceActionSetup` — id, type, direction, phase, times, confidence, reference/entry levels, source event ids
- `PriceActionSnapshot.Setups` — emitted each bar (armed + any terminal transitions this bar)
- Helpers: `HasTriggeredSetup`, `GetBestTriggeredSetup`

### Composer state machines

Stateful **per chart key** (instrument + interval) via `AnalysisState.SetupComposer`.

#### 1) Break / CHOCH → retest hold

| Step | Source | Result |
|---|---|---|
| Arm | `Bullish/BearishBreakOfStructure` or `…ChangeOfCharacter` | `Bullish/BearishBreakRetestHold` or `…ChoChRetestHold` Armed |
| Trigger | `…RetestHeld` event or `ActiveRetest.State == RetestHeld` | Phase `Triggered` |
| Invalidate | `ActiveRetest` failed (deep close through) | `Invalidated` |
| Expire | `ActiveRetest` expired | `Expired` |

#### 2) Sweep → displacement

| Step | Source | Result |
|---|---|---|
| Arm | Sell-side sweep (bullish) / buy-side sweep (bearish) | `…SweepDisplacement` Armed |
| Trigger | Same-direction `…Displacement` within `SweepFollowThroughBars` (default 5) | `Triggered` |
| Invalidate | Opposing BOS/CHOCH | `Invalidated` |
| Expire | No displacement in window | `Expired` |

#### 3) Sweep → CHOCH / BOS

| Step | Source | Result |
|---|---|---|
| Arm | Liquidity sweep | `…SweepChoCh` Armed |
| Trigger | Same-direction CHOCH or BOS within `ChoChFollowThroughBars` (default 8) | `Triggered` |
| Invalidate | Opposing BOS/CHOCH | `Invalidated` |
| Expire | Timeout | `Expired` |

### Options

`PriceActionSetupOptions` (on `ChartAnnotationOptions.PriceActionSetups`):

- `SweepFollowThroughBars`, `ChoChFollowThroughBars`
- `MinimumSetupConfidence` (default 55)
- `EnabledSetups` list

### Important properties

- Non-repainting: only completed candles / already-emitted atomic events.
- Same TF only: composer never reads other intervals.
- Armed setups are re-emitted every bar until terminal so dashboards/agents can see live state.

## Phase 2 — Multi-timeframe policy

### `MultiTimeframePriceActionPolicy`

**Input:** HTF (trend) snapshot + LTF (entry) snapshot + expected direction + confirmation mode.

**`BuildContext(trend)`** sets:

- `Bullish/BearishContinuationArmed` — HTF break-retest setup or rising/falling structure + bias
- `Bullish/BearishReversalArmed` — HTF CHOCH / sweep setups or atomic CHOCH/sweep events
- `ContextLevel` — HTF setup reference when available
- Structure veto: falling structure clears bullish continuation; rising clears bearish continuation

**`EvaluateEntry(...)`** returns `PriceActionGateResult` (`Allowed`, reason, context, triggered LTF setup, confidence boost).

### Confirmation modes (`PriceActionConfirmationMode`)

| Mode | Behavior |
|---|---|
| `Disabled` | No PA gate |
| `Soft` | Always allow; setup/context only boost confidence and annotate |
| `Required` | Need LTF `Triggered` setup **or** legacy `HasConfirmedTrigger` atomic event |
| `RequiredWithContext` | Required + HTF arm (`RequireHtfArm`) + optional proximity of LTF level to HTF `ContextLevel` + continuation/reversal family check |

CLI: `--price-action-mode required-with-context` (aliases: `context`, `mtf`).

### Options (`MultiTimeframePriceActionOptions` on progressive options)

- `RequireHtfArm` (default true for RequiredWithContext path)
- `VetoAgainstHtfStructure` (default true)
- `ContextLevelProximityAtr` (default 0.75, measured in **entry TF ATR**)
- `MinimumHtfSetupConfidence` / `MinimumLtfSetupConfidence`
- `HtfArmExpiryBars` (default 20; stale setup context is not allowed to arm an entry)

## Agent wiring (orders)

### Progressive entry gate

In `ProgressiveStrategyBase`, after confirmation/entry window ready:

1. Reject strong opposing LTF PA bias (existing).
2. `EvaluateEntry(trend, entry, mode, mtfOptions, allowedSetups)`.
3. If not allowed → `Observe` with gate `ReasonCode`.
4. **Entry trigger** (either is enough):
   - classic `DetectSide` on the entry TF, **or**
   - triggered PA setup / atomic PA trigger / aligned PA bias score
5. If allowed → `CreateEntryDecision(..., priceActionSetup)`, then annotate:

### Improved agent signal path (`ImprovedProgressiveAgent`)

Common reasons improved entries used to fail:

| Failure | Fix |
|---|---|
| Entry required `DetectSide` (RSI+structure) even after PA retest | PA setup/trigger/bias can fire entry |
| Nearest credible obstacle &lt; minimum R:R | Reject with `RewardBlockedByStructure`; do not manufacture reward through resistance/support |
| Micro swing stop invented huge R, distant swing killed R | Stop band ~0.35–2.75 ATR; prefer ~1 ATR structural stop |
| PA reference ignored when only Armed | Also use Armed/Triggered setup references for stops |

Stop priority: PA setup reference → entry swing/zone → higher-timeframe structure → channel → ATR fallback.  
Target priority: nearest credible opposing zone/swing/channel with a fill buffer. If that obstacle cannot satisfy minimum R:R, the trade is rejected. A min-R/ATR projection is used only when no credible structural obstacle exists.

   - `PriceActionSetupType`, `PriceActionSetupId`, `PriceActionSetupReferenceLevel`
   - `PriceActionTrigger` (mapped from setup type when useful)
   - `PriceActionConfidence`, `ReasonCode`, confidence boost
   - Reason text includes setup type and HTF context

### Stop placement

- **ImprovedProgressiveAgent** and **LegacyProgressiveAgent** prefer stop beyond `GetBestTriggeredSetup(...).ReferenceLevel` + ATR buffer when the level is on the correct side of price.
- Fallback remains swing / channel / ATR as before.

### Strategy options

`ProgressiveStrategyOptions`:

- `PriceActionConfirmation` (includes `RequiredWithContext`)
- `MultiTimeframePriceAction`
- `AllowedPriceActionSetups` (empty = all core setups)
- `RsiBollingerSignals` — direction-aware RSI relationship/momentum and Bollinger `%B`/width confirmation. Aligned divergence/convergence or squeeze release can trigger; strong opposing evidence vetoes.
- `ZoneVolumeSignals` — direction-aware support/resistance and same-timeframe relative-volume confluence. These strengthen an RSI relationship; neither a zone nor volume invents a direction on its own.

### RSI/Bollinger confluence beside price action

`RsiBollingerSignalPolicy` is an independent entry-evidence layer shared by the
progressive and reference rule-based agents. It does not reinterpret an unresolved
Bollinger squeeze as bullish or bearish. A signal requires one of:

- recent aligned RSI regular/hidden divergence or convergence plus current RSI
  momentum or aligned Bollinger location; or
- a squeeze release with `%B` beyond the configured directional breakout threshold.

Strong recent opposing RSI relationships and an expanding band move far into the
opposing `%B` region veto the order. The policy contributes a bounded confidence
adjustment and writes its evidence into the decision reason.

### Zone and volume confluence

`ZoneVolumeSignalPolicy` consumes the zones and broker candle volume already present in
the analysis snapshot. A credible supporting zone is selected only on the correct side
of price and inside a configurable ATR radius; an opposing zone is penalized only while
it is close enough to matter. An RSI relationship confirmed at that zone receives extra
weight.

Volume is compared with the prior rolling median for the same instrument, timeframe,
and `VolumeKind`. It confirms only when the completed candle direction matches the
candidate. Tick count is reported as activity, while quantity feeds retain their actual
semantic. Low volume is a small penalty and an opposing spike is a veto. Unknown volume
kind is ignored by default, and a squeeze release needs both directional `%B` and aligned
elevated volume to override a nearby-zone indicator veto.

For progressive structural/PA entries, the improved agent's obstacle-aware target and
minimum-R logic remains the hard zone decision. This avoids counting the same resistance
or support twice. The confluence policy can still trigger an otherwise qualified
indicator entry and its bounded adjustment is recorded in the decision reason.

## How to debug “why no entry?”

| Symptom | Check |
|---|---|
| No setups on snapshot | Heavy analysis path / atomic events not firing; composer only arms from events |
| Armed but never Triggered | Retest hold / displacement / CHOCH follow-through missing |
| Required blocked `LtfSetupNotTriggered` | No Triggered setup and no atomic trigger on entry TF |
| `HtfContextNotArmed` | RequiredWithContext and HTF has no continuation/reversal arm |
| `HtfStructureVeto` | Trading against HTF structure without reversal arm |
| `ContextLevelTooFar` | LTF setup level far from HTF reference vs `ContextLevelProximityAtr` |
| `SetupFamilyMismatch` | HTF reversal-only arm but LTF continuation retest |

## Files touched (implementation map)

| File | Role |
|---|---|
| `ChartAnnotator/Models/AnalysisModels.cs` | Setup models + snapshot helpers |
| `ChartAnnotator/PriceAction/PriceActionSetupOptions.cs` | Composer options |
| `ChartAnnotator/PriceAction/PriceActionSetupComposer.cs` | Phase 1 state machines |
| `ChartAnnotator/Engine/ChartAnnotationEngine.cs` | Wire composer after analyzer |
| `ChartAnnotator/Engine/ChartAnnotationOptions.cs` | `PriceActionSetups` |
| `Agent/Strategies/MultiTimeframePriceActionPolicy.cs` | Phase 2 MTF gate |
| `Agent/Strategies/ProgressiveStrategyOptions.cs` | Modes + MTF options |
| `Agent/Strategies/ProgressiveStrategyBase.cs` | Entry gate + decision annotation |
| `Agent/Strategies/ImprovedProgressiveAgent.cs` | Setup-aware stop |
| `Agent/Strategies/LegacyProgressiveAgent.cs` | Setup-aware stop |
| `Agent/Models/AgentModels.cs` | Decision setup fields |
| `BacktestRunner/BacktestCommandOptions.cs` | CLI mode parse |

## Simulator “no trades” notes (2026-07-15)

Investigation of dashboard/streaming runs found several real blockers that prevent agents from opening positions even when the progressive stack is healthy:

1. **Risk-based position sizing + missing FX conversion**  
   Dashboard defaults use `FixedFractionalRisk`. If the account currency is not the instrument quote and no `QuoteToBaseCurrencyRates` entry exists, sizing returned `MissingCurrencyConversion` and **every entry was rejected**.  
   **Resolution:** the simulator now defaults a single-instrument account to the instrument quote currency when appropriate, and callers can supply an explicit conversion. If a required conversion is still missing, the order is deliberately rejected. Risk-based modes never fall back to a strategy quantity because that would bypass the configured risk budget.

2. **Double risk clamp**  
   Session risk options also enforced `MaximumLossPercentageOfBalance = 0.5` (0.5% of equity). Fixed-quantity fallbacks often exceeded that and were rejected again.  
   **Fix:** that secondary clamp is disabled in strategy sessions; the sizer owns the risk budget.

3. **Entry window too short**  
   Defaults were `MaximumEntryCandles = 3` (15 minutes on 5m entry) and one trend bar for confirmation.  
   **Fix:** defaults are now `MaximumEntryCandles = 12` and `MaximumTrendConfirmationBars = 3`.

4. **Incomplete dashboard runs**  
   Several local sims under `Dashboard/public/data/simulations/*/INCOMPLETE` never finished evaluation, so the UI shows no trades even though the agent path can trade (a completed Jan-2025 GBPJPY run had 84 legacy / 11 improved trades).

When debugging a zero-trade run, check journal lines for `Position sizing rejected`, `MissingCurrencyConversion`, `PrimaryTrendNotReady`, `EntryTriggerNotReady`, and `OpposingPriceAction`.

## Intentionally not in Phase 1–2

- Projecting HTF channels/trendlines onto LTF charts
- Compression-hold composite (can add later)
- LLM/AI detection
- Changing risk/execution pipelines beyond decision metadata and stop preference

## Extension checklist

1. New composite: add `PriceActionSetupType`, arm/trigger rules in composer, enable in options, tests.
2. New MTF arm rule: extend `BuildContext`.
3. Stricter agent: set `PriceActionConfirmation = RequiredWithContext` and optionally restrict `AllowedPriceActionSetups`.
