# TradingHub — Session/Spread Filter Configuration Adjustment

**Purpose:** Increase valid trade opportunities without removing essential market-safety protection.  
**Current decision:** Apply configuration changes only. Do not change the trading code in this pass.  
**Target:** Simulator first, then reuse the validated configuration for observe-only and OANDA demo operation.

---

## 1. Problem

The Improved Agent produced only two trades during a one-month EUR/USD simulation.

The session/spread condition filter is likely removing or heavily reducing a significant number of otherwise valid Agent candidates.

The filter currently operates after the Agent has already passed several stages:

```text
Primary trend
    → secondary trend
    → setup interval
    → confirmation interval
    → entry trigger
    → price-action checks
    → regime routing
    → session/spread condition filter
    → risk approval
    → order
```

Adding aggressive session and spread constraints on top of an already selective strategy can starve the strategy of trades.

---

## 2. Current relevant configuration fields

The Dashboard/API already exposes the required settings:

```text
TradingConditionsEnabled
AllowedSessions
RolloverBlackoutMinutesBefore
RolloverBlackoutMinutesAfter
ConditionSoftSpreadAtr
ConditionHardSpreadAtr
EconomicEventFilterEnabled

RegimeEnabled
RegimeSoftSpreadAtr
RegimeHardSpreadAtr
```

The runtime models also contain:

```text
SoftSpreadRiskMultiplier
RejectWhenAtrUnavailable
ReduceRiskWhenSpreadUnavailable
MissingSpreadRiskMultiplier
PreWeekendMinutes
MaximumDataAge
```

No code change is needed to widen the allowed sessions or relax the existing spread/ATR limits.

---

## 3. Important current behaviour

### 3.1 Session restriction

When the current session is not included in `AllowedSessions`, the filter produces:

```text
Action: DelayEntry
Reason: SessionNotAllowed
Risk multiplier: 0
```

The current pipeline does not persist that candidate and retry it later. For the current implementation, this behaves similarly to discarding the candidate.

Therefore, the safest configuration-only mitigation is to allow every normal open Forex session.

### 3.2 Spread thresholds

The current default thresholds are:

```text
Soft spread/ATR: 0.15
Hard spread/ATR: 0.30
```

Behaviour:

```text
spread/ATR below soft threshold:
    normal risk

spread/ATR between soft and hard:
    allow with reduced risk

spread/ATR at or above hard threshold:
    reject entry
```

These thresholds can be aggressive for lower-timeframe entries because ATR may be small even when the absolute spread is normal.

### 3.3 Regime and condition spread limits

The market-regime classifier has its own spread/ATR thresholds.

Both sets should be changed together:

```text
ConditionSoftSpreadAtr
ConditionHardSpreadAtr

RegimeSoftSpreadAtr
RegimeHardSpreadAtr
```

Otherwise one subsystem may allow a setup while the other classifies the market as unsafe.

---

## 4. Immediate recommended configuration

Use the following for the next five-minute-entry research run:

```json
{
  "tradingConditionsEnabled": true,
  "allowedSessions": [
    "Asian",
    "London",
    "NewYork",
    "LondonNewYorkOverlap"
  ],
  "rolloverBlackoutMinutesBefore": 15,
  "rolloverBlackoutMinutesAfter": 15,
  "conditionSoftSpreadAtr": 0.25,
  "conditionHardSpreadAtr": 0.50,
  "economicEventFilterEnabled": false,

  "regimeSoftSpreadAtr": 0.25,
  "regimeHardSpreadAtr": 0.50
}
```

### Meaning

```text
All normal open sessions:
    allowed

Ordinarily elevated spread:
    trade with reduced risk

Very high spread:
    reject

Broker rollover:
    continue to block

Economic-event filter:
    disabled until a real versioned event provider is configured
```

These values are research defaults, not final production parameters.

---

## 5. Dashboard values

In the Simulator panel, set:

```text
Session/spread condition filter:
    Enabled

Allowed sessions:
    Asian,London,NewYork,LondonNewYorkOverlap

Rollover blackout before:
    15

Rollover blackout after:
    15

Condition soft spread/ATR:
    0.25

Condition hard spread/ATR:
    0.50

Economic-event filter:
    Disabled

Regime soft spread/ATR:
    0.25

Regime hard spread/ATR:
    0.50
```

Be careful when selecting presets:

### Day-trading preset

The current day-trading preset uses:

```text
London,NewYork,LondonNewYorkOverlap
```

It excludes `Asian`.

### Scalping preset

The current scalping preset also excludes `Asian` and uses a wider rollover blackout.

### Risk-managed preset

The current risk-managed preset keeps all normal sessions, but still inherits the strict default spread thresholds unless manually changed.

After selecting a preset, review the session and spread fields before starting the run.

---

## 6. Controlled comparison plan

Do not judge the configuration from a single run.

Use the exact same:

- instrument;
- dates;
- candle source;
- warm-up;
- strategy;
- timeframes;
- position sizing;
- execution costs;
- management options;
- random seed/configuration identity where applicable.

Only change the condition-filter configuration.

### Run A — current configuration

Keep the configuration that produced the original low-trade result.

### Run B — relaxed configuration

Use:

```text
All four normal sessions
Soft spread/ATR = 0.25
Hard spread/ATR = 0.50
Economic-event filter disabled
Matching regime spread limits
```

### Run C — condition-filter ablation

For diagnosis only:

```text
TradingConditionsEnabled = false
```

Do not use Run C as the final OANDA demo configuration. It removes useful stale-data, rollover and cost protection.

---

## 7. Metrics to compare

For each run, record:

```text
Agent Buy/Sell candidates
SessionNotAllowed count
BrokerRolloverBlackout count
PreWeekendBlackout count
SpreadAtrSoftLimit count
SpreadAtrHardLimit count
SpreadUnavailable count
AtrUnavailable count
Trading-condition approved count
Risk-approved count
Orders submitted
Trades filled
Net P/L
Average R
Profit factor
Maximum equity drawdown
Average MFE
Average MAE
Average MFE giveback
Session-risk reductions
Execution-cost reductions
```

Group the results by:

```text
Strategy
Instrument
Direction
Entry timeframe
Trading session
Regime
Spread/ATR bucket
Reason code
```

---

## 8. Interpretation rules

### The relaxed configuration is useful when:

- trade count rises meaningfully;
- average R remains positive;
- profit factor does not collapse;
- drawdown remains controlled;
- additional trades are not concentrated in very poor sessions;
- spread costs remain acceptable.

### The relaxed configuration is too loose when:

- trade count rises but expectancy becomes negative;
- most new trades occur in high spread/ATR conditions;
- average MAE rises sharply;
- drawdown increases disproportionately;
- execution costs consume the added gross profit.

### The Agent is still over-filtered when:

- disabling the condition filter produces few additional candidates;
- most candidates are already rejected earlier by trend, setup, confirmation, price action or regime rules.

In that case, use the FeatureSwitch ablation system rather than loosening the condition filter further.

---

## 9. Recommended initial interpretation of results

Use this rough diagnostic:

```text
Run A: 2 trades
Run B: 6–12 trades
Run C: 8–15 trades
```

This would indicate the condition filter was a major bottleneck and the relaxed configuration recovers most useful opportunities.

If:

```text
Run A: 2 trades
Run B: 2–3 trades
Run C: 3 trades
```

then the condition filter is not the primary problem. The low frequency is likely caused by Agent setup gates.

If:

```text
Run A: 2 trades
Run B: 15 trades
Run C: 40 trades
```

the relaxed configuration is still rejecting many candidates. Inspect hard spread rejection and session reason counts before changing thresholds again.

---

## 10. Configuration for later OANDA demo observation

For observe-only OANDA operation, start with the same research configuration:

```text
Allowed sessions:
    Asian
    London
    NewYork
    LondonNewYorkOverlap

Condition soft spread/ATR:
    0.25

Condition hard spread/ATR:
    0.50

Regime soft spread/ATR:
    0.25

Regime hard spread/ATR:
    0.50

Rollover blackout:
    15 minutes before and after

Economic event filter:
    disabled until a real provider is installed
```

Observe and record the real OANDA spread/ATR distribution by:

```text
Instrument
Session
Entry timeframe
Day of week
Regime
```

The later calibrated thresholds should come from those real distributions.

---

## 11. What is intentionally postponed

No code changes are required in this pass.

The following improvements are useful later but are explicitly postponed:

### Genuine deferred-entry behaviour

Persist a candidate blocked by session or temporary spread, then revalidate it later.

Current configuration mitigation:

```text
Allow all normal sessions.
```

### Session-specific risk multipliers

Example future behaviour:

```text
London/overlap:
    1.00 risk

New York:
    0.90 risk

Asian:
    0.60 risk
```

The current runtime has one soft-spread multiplier, not a general per-session risk schedule.

### Percentile-calibrated spread thresholds

Future thresholds should be calibrated by:

```text
Instrument × timeframe × session
```

For example:

```text
Normal:
    below historical 80th percentile

Reduced risk:
    80th–95th percentile

Reject:
    above 95th or 97th percentile
```

### Condition-filter funnel Dashboard

A future Dashboard improvement should expose every condition-filter reason and count.

The research runner and FeatureSwitch system should eventually automate this analysis.

---

## 12. No-code-change acceptance criteria

This configuration-only change is complete when:

1. the same historical month is run under current, relaxed and disabled-filter configurations;
2. all four normal sessions are present in Run B;
3. condition and regime spread thresholds are aligned;
4. economic-event filtering is disabled without a configured provider;
5. rollover protection remains enabled;
6. the comparison records rejection/reduction counts;
7. trade frequency improves without unacceptable deterioration in expectancy or drawdown;
8. the selected configuration is saved with its configuration hash;
9. no source-code changes are made as part of this task.

---

## 13. Final instruction to the next AI

Do not modify the condition-filter implementation in this task.

Only:

1. apply the configuration values in this document;
2. run the controlled comparison;
3. extract the condition/rejection diagnostics;
4. compare performance;
5. recommend whether to retain, tighten or loosen the configuration;
6. document the exact configuration hash for every run.

Do not:

- disable all safety filtering for OANDA autonomous trading;
- claim `DelayEntry` is genuinely deferred;
- enable economic-event filtering without a real provider;
- use different regime and condition spread limits accidentally;
- increase position size merely because trade count is low;
- optimise thresholds from one profitable month alone.

---

## 14. Recommended configuration summary

```text
TradingConditionsEnabled:
    true

AllowedSessions:
    Asian, London, NewYork, LondonNewYorkOverlap

ConditionSoftSpreadAtr:
    0.25

ConditionHardSpreadAtr:
    0.50

RegimeSoftSpreadAtr:
    0.25

RegimeHardSpreadAtr:
    0.50

RolloverBlackoutMinutesBefore:
    15

RolloverBlackoutMinutesAfter:
    15

EconomicEventFilterEnabled:
    false
```

This is the next configuration to test.
