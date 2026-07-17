# Profit Protection and Long-Run Trade Feed Fix

## Scope

This implementation adds deterministic post-entry profit protection to the streamed comparative simulator and hardens the live trade feed used during long runs.

The position-management layer now supports:

- staged scale-out and a protected runner;
- cost-aware break-even;
- swing/zone/channel trailing with an ATR buffer;
- profit-floor ratchets;
- maximum giveback from MFE;
- stagnation reduction;
- adverse-structure reduction;
- confirmed momentum-decay reduction;
- confirmed post-expansion volatility-exhaustion reduction;
- optional UTC risk-window reduction;
- optional spread/ATR execution-cost-stress reduction;
- daily account-equity profit-target and giveback entry locks.

These are recommendations from `TradeManager`; mutation remains an `ExecutionManager` and broker responsibility.

## Deterministic priority

For an open position, management uses this priority:

1. existing stop or target fill;
2. emergency/safety liquidation;
3. explicit Agent close;
4. TradeManager full-exit rule;
5. one position-reduction recommendation;
6. one risk-reducing stop amendment;
7. hold.

A reduction and stop amendment may be submitted from the same completed management candle. Both become eligible on the next execution frame. Market reductions execute before stop/target orders, and protective quantities are reconciled atomically to the remaining simulated position.

## Staged scale-out

Legacy defaults:

- close 20% of initial quantity at `+1R`;
- close another 20% at `+1.5R` or near opposing structure;
- preserve at least a 40% runner.

Improved defaults:

- close 15% at `+1R`;
- close another 15% at `+2R` or near opposing structure;
- preserve at least a 50% runner and retain the bracket target.

A stage is considered complete only after a confirmed fill. A pending stage blocks duplicate requests. Every other reduction rule is also clamped so the configured runner is not breached.

## Profit floor and MFE giveback

R is always measured from the immutable initial stop:

```text
initial risk = abs(entry - initial stop)
```

Moving the stop or reducing quantity never changes the R denominator.

For a long trade, the final proposed stop is the highest valid risk-reducing candidate among:

- current stop;
- cost-adjusted break-even;
- confirmed structural level minus ATR buffer;
- profit-floor price;
- MFE-giveback floor.

For a short trade, the inverse selection is used.

A candidate is rejected when it widens risk, is on the wrong side of executable price, or fails the minimum ATR/tick improvement.

## Momentum and volatility protections

Momentum-decay reduction requires at least two confirmations, and at least one must be a recent adverse RSI divergence or a current adverse price-action event. ADX weakening or adverse RSI direction can provide the second confirmation.

Price-action events are taken from the current completed analysis snapshot. Their market-event sequence is deliberately not compared with `AnalysisSnapshot.Version`, because those are different sequence domains.

Volatility-exhaustion reduction requires all of the following:

- a Bollinger expansion or wide regime was observed after entry;
- Bollinger width is now contracting;
- price loses the middle band in the adverse direction;
- ADX is weakening;
- no new favourable structure break is confirmed.

Contraction without a prior expansion is not exhaustion.

## Reduce-only safety

All `AgentAction.Close` orders are emitted as broker-neutral `ReduceOnly` orders.

- OANDA maps this to `positionFill = REDUCE_ONLY`.
- The simulated broker rejects a reduce-only order with no opposite open position.
- It rejects a reduce-only order that would add exposure.
- If a stale close quantity exceeds the remaining position, the simulated fill is clamped to the remaining quantity and cannot reverse the position.
- Protective orders are never cancelled before a close fill is confirmed.
- After a partial fill, stop and target quantities are atomically resized.
- After a full fill, attached protective orders are cancelled.

Native live-broker partial-close and protective-order reconciliation still requires broker-specific certification before live use.

## Replay and diagnostics

The simulator records:

- `PartialExitRecommended`;
- `PartialExitSubmitted`;
- `PartialExitAccepted`;
- `PartialExitRejected`;
- `ProtectiveQuantityUpdated`;
- `RunnerActivated`;
- `ProfitFloorActivated`;
- `MaximumGivebackProtectionActivated`;
- `TradeManagementEvaluated`;
- `SafetyStateChanged`.

Each partial exit stores quantity before/after, fill price, gross/net P/L, allocated entry commission, exit commission, realised R, reason, structural evidence, and execution sequence.

The Dashboard chart displays partial exits and the stepped stop-amendment line. Performance includes reductions, runner activations, partial-exit P/L, floor/giveback exits, and reduction counts by reason.

## Long-run `/trades` HTTP 500

The reported failure was caused by calling LINQ on a null `Items` collection from `trades.index.json`. This can happen with an older schema or a partially written/index file recovered during a long run.

The endpoint now:

- treats missing/null `Items` as an empty collection;
- validates ordinal, offset, length, setup ID, and file bounds;
- avoids offset arithmetic overflow;
- skips and logs corrupt index entries;
- skips and logs corrupt NDJSON rows;
- advances the cursor over every inspected entry, including corrupt rows, so polling cannot become stuck retrying one bad item;
- continues serving valid trades and replay data.

## Account-level protection

`TradingSafetyOptions` supports optional account-currency limits:

```csharp
new TradingSafetyOptions
{
    DailyEquityProfitTarget = 500m,
    DailyEquityGivebackActivation = 350m,
    MaximumDailyEquityGiveback = 150m
}
```

These controls pause new entries for the rest of the UTC day. They do not force-close protected open positions. Existing daily/weekly loss and consecutive-loss trips remain separate.

## Validation focus

The test suite includes scenarios for:

- staged reduction only once;
- runner preservation;
- profit-floor and MFE-giveback ratchets;
- stagnation reduction;
- momentum confirmation;
- prior-expansion requirement for volatility exhaustion;
- UTC risk window crossing midnight;
- partial-close protective quantity reconciliation;
- target fill after partial close without position reversal;
- two stale reduce-only closes becoming eligible together without reversal;
- daily equity profit lock and daily giveback reset;
- sequential/parallel deterministic trailing behaviour.
