# Experimental price-structure invalidation

Enable with `--alfonso-invalidate-price-structure`; omit it for baseline behavior.
This switch is independent of `--alfonso-maintain-structure` and applies to each
configured timeframe using only that timeframe's closed candles.

A strict price swing needs two candles on each side. An established downtrend
becomes OutOfAlignment when a candle closes above the latest confirmed high;
an uptrend does so on a close below the latest confirmed low. Wicks and equal
closes do not count. Invalidation takes precedence over a same-bar opposite
elimination signal, so it cannot directly reverse the trend. The old direction
cannot re-establish while price remains beyond its confirmed swing.

The existing scenario/candidate path refuses neutral top-timeframe entries and
cancels tracked resting entries that are no longer valid. Open bracketed
positions are unchanged. Stops and targets are unchanged.

This is a price-pivot invalidation experiment, not the older zone-swing
maintenance switch and not a proven profitability improvement. Compare it alone
against the unchanged baseline before combining it with other experiments.

Verification: 125 Alfonso regression tests passed (including eight new cases
for this switch); BacktestRunner built with no warnings or errors. No full
multi-market performance replay is claimed by these checks.
