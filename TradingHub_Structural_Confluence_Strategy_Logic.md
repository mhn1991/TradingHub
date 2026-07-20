# Structural Confluence Strategy — How It Actually Works

This explains the *decision logic* of the Structural Confluence agent: what it looks at, what
has to line up before it will trade, and why. No code — this is the mental model.

## 1. The three timeframes it always uses

Every evaluation looks at exactly three chart timeframes, each with a fixed job:

| Role | Example | Job |
|---|---|---|
| **Context** | 2h | The higher-timeframe backdrop — is the market trending up, down, ranging, or in a dangerous/illiquid regime? Optionally backed up by one or more **additional context** timeframes (e.g. 1h) that get averaged in for a steadier read. |
| **Setup** | 30m | Where the actual liquidity pools, supply/demand zones, and their events (sweeps, breaks, retests) are tracked. This is the timeframe the *catalyst* for a trade comes from. |
| **Trigger** | 5m | Where the agent looks for a price-action confirmation (a rejection, displacement, retest-held, break of structure, etc.) and reads momentum indicators (CCI, ADX, Bollinger) at fine resolution. |

The agent re-evaluates every time the trigger timeframe closes a new candle — for a 5m trigger,
that's every 5 minutes. It only ever acts once per instrument per moment: if a position is
already open for this strategy, it does nothing until that position closes.

## 2. Three independent playbooks, each betting on a different pattern

The agent runs three separate playbooks every evaluation (whichever ones you've enabled). Each
one independently asks "is *my* pattern present right now?" and produces its own verdict.

### Playbook A — Liquidity Sweep Reversal
**Thesis:** price hunts resting stops beyond an obvious level (a prior swing high/low, equal
highs/lows, a round number, a previous session/day/week high or low), fails to hold beyond it,
and reverses.

- Looks for the most recent **sweep** — a candle that pierced through a known liquidity pool and
  then closed back inside it.
- Trades in the *opposite* direction of the sweep (a sweep of a sell-side pool, e.g. equal lows,
  is a **bullish** reversal signal, and vice versa).
- Wants the sweep to be **fresh** (within a configurable number of trigger-timeframe bars — the
  window is genuinely short, on the order of an hour by default) and not already **superseded**
  by price accepting a break through the same pool afterward.
- Optionally wants a nearby supply/demand zone on the same side to add confluence, at a
  configurable strictness (disabled / preferred / required).
- Wants the pool itself to be decent quality, the reclaim candle's rejection to be strong enough,
  a price-action trigger nearby, and (depending on mode) CCI momentum to agree.

### Playbook B — Supply/Demand Pullback
**Thesis:** price is trending, pulls back into a fresh, unmitigated demand (in an uptrend) or
supply (in a downtrend) zone, and resumes.

- Picks the *nearest* eligible zone to current price, preferring one whose side already agrees
  with the higher-timeframe context.
- Requires the zone to still be in an acceptable state (not fully mitigated/invalidated), not
  overused (touch-count and penetration limits), and either freshly reacting (approached/touched)
  or price sitting right at it.
- By default requires the higher-timeframe context to actually agree with the zone's direction
  (a demand zone only trades long if the context is bullish) — with an optional allowance for
  range-boundary context instead of a hard trend.
- Confirmation can come from CCI, or — if enabled — from RSI momentum or a Bollinger re-entry as
  an alternative to CCI.

### Playbook C — Liquidity Break & Retest
**Thesis:** price decisively breaks and *closes beyond* a liquidity pool (not just wicks through
it), optionally comes back to retest the broken level, and continues in the breakout direction.

- Looks for the most recent **accepted break** of a pool (a close-based breakout, held for a
  minimum number of bars) that hasn't since **failed** (reversed back through).
- Requires a minimum displacement (how far price moved away from the pool, in ATR) so a
  barely-there break doesn't qualify.
- Can optionally require an actual **retest** event (price coming back to the broken level) and
  that the retest stayed within a maximum distance of it.
- Can optionally require "expansion" confirmation — ADX trend strength, efficiency ratio, or
  Bollinger-band expansion agreeing with the breakout direction — on top of or instead of CCI.

## 3. The gate system every playbook shares

Each playbook doesn't produce a single yes/no — it runs a **checklist of mandatory gates**
(pool/zone quality, freshness, trigger presence, confirmation, and so on, specific to that
playbook). Every gate must pass for the playbook to be "ready." Each gate also carries a numeric
quality score.

**Confidence** is then computed as: *the lowest-scoring gate's quality*, plus small adjustments
for how well the higher-timeframe context agrees with the trade direction and how strong the
confirmation signal (CCI/RSI/etc.) is. If even one gate failed, confidence is forced to zero —
confidence only matters as a final check once everything else already lines up. A playbook only
becomes an actual trade candidate if every gate passed **and** confidence clears a configurable
minimum (55 by default).

If a gate fails, the agent records *which* gate failed as a reason code — this is exactly what
lets you see why a particular moment didn't produce a trade (see §6).

## 4. Position geometry — where the stop and target go

Once a playbook is "ready," it still has to produce valid trade geometry, or the candidate is
thrown out anyway. There are two different ways this happens, controlled by whether **adaptive
target management** is turned on for the profile:

### Without adaptive targets (the default, "v1")
- **Stop**: placed just beyond whatever invalidates the pattern (the sweep's extreme, the zone's
  far edge, or the broken pool's edge), plus a small ATR buffer.
- **Target**: the *nearest* obstacle beyond entry — the closest opposing swing, liquidity pool, or
  supply/demand zone — minus a small ATR buffer. There's no ranking by "best" target; whichever
  obstacle is closest wins.
- The reward (entry to target) divided by the risk (entry to stop) must clear a **minimum
  reward:risk** (1.5 by default). If the nearest obstacle doesn't clear that bar, the whole trade
  is rejected — even though every playbook gate passed.

This is the strategy's single biggest built-in tension: the same structural detection that finds
your entry catalyst also fills the space between entry and any further level with more structure,
which is exactly what the target search then hits first. The busier the chart's own structure,
the *closer* the nearest obstacle sits, and the harder the R:R gate becomes to clear.

### With adaptive target management ("v2")
Instead of "nearest obstacle wins," a **tiered target map** is built: candidate targets (swings,
pools, zones) are scored and ranked into tiers by quality/prominence, and the trade is planned as
a sequence — a checkpoint, partial exits at intermediate tiers, and a final runner target — rather
than one fixed take-profit. Each playbook also picks a different **exit policy** depending on
context:

- **Fixed structural target** — one hard take-profit, same spirit as v1 (used when the setup is
  more counter-trend/range-bound in nature, e.g. a sweep reversal without trend alignment).
- **Partial-then-runner** — scale out at intermediate targets, let a runner ride further (used
  when the higher-timeframe context agrees with the trade direction).
- **Managed expansion** — a looser, trend-following exit used specifically by the break/retest
  playbook when volatility/trend-strength confirms real expansion is underway.

Turning on adaptive targets also changes how the agent manages *risk before entry*: instead of
always demanding a hard take-profit order be attachable, it's allowed to run under a mode where
the trade manager is responsible for the exit — this only matters for the v2 path.

## 5. Confirmation modes (CCI, and friends)

Every playbook checks momentum confirmation, most commonly via CCI, with three modes:

- **Disabled** — confirmation is ignored entirely (only affects the confidence adjustment, never
  blocks a trade).
- **Soft** (default) — confirmation is never a hard requirement; alignment nudges confidence up,
  conflict nudges it down, but a conflicting reading alone won't reject the setup.
- **Required** — the playbook will not trade unless CCI is genuinely aligned with the trade
  direction (or, for the pullback playbook, an allowed alternative like RSI momentum or a
  Bollinger re-entry substitutes for it).

"Aligned" isn't just "CCI is above/below zero" — it also looks at CCI *divergences* against price
(regular/hidden bullish or bearish divergence, or a bullish/bearish convergence pattern) as an
independent way to call alignment.

## 6. What happens when multiple playbooks fire at once

If more than one enabled playbook is ready in the same evaluation:

- If they all agree on **direction**, the single best one is chosen — ranked by its weakest gate's
  quality, then geometry quality, then confidence, then how recent its catalyst is. A small
  confidence bonus is added for having multiple playbooks agree.
- If they **disagree on direction** (one wants long, another wants short, in the same moment),
  neither trades — the whole evaluation is thrown away as a conflict, on the theory that
  simultaneous opposing signals mean the read is genuinely ambiguous right now.

## 7. Observed in practice (this account's real EUR/USD data, May–June 2026)

Replaying real cached 1-minute EUR/USD data through the exact same evidence pipeline the live
strategy uses turned up the following, for a profile with only the sweep-reversal playbook
enabled and adaptive targets *off* (v1):

- Sweeps themselves aren't rare — about 700 sweep events occurred over roughly 4.5 weeks at the
  30-minute setup interval, and nearly all of them (>99%) pass the raw penetration/reclaim math.
- But because a sweep is only "fresh" for a short window (roughly two setup-timeframe bars) while
  the agent re-evaluates every 5 minutes, most individual evaluations land *between* sweeps and
  correctly report "no usable sweep right now" — this is expected behavior, not a bug.
- The dominant blocker turned out to be **§4's reward:risk gate**: measuring the v1 nearest-obstacle
  geometry against every real sweep that occurred, only about **7%** cleared the 1.5 minimum
  reward:risk — the median achievable R:R was around 0.14. The nearest obstacle sits, on average,
  far closer to entry than the stop distance, so the geometry gate rejects the overwhelming
  majority of otherwise-valid setups.

In short: this account's structural profile isn't broken — the playbook and evidence detection
are working as designed. It's specifically the v1 fixed-target geometry's "nearest obstacle must
clear 1.5R" rule that is, in practice, almost never satisfied on this instrument/timeframe
combination. The two most direct levers to test are turning on adaptive target management (which
replaces "nearest obstacle" with a ranked, multi-tier target selection) and/or lowering the
minimum reward:risk requirement.
