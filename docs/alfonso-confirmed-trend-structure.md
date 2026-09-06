# Experimental trend confirmation

Enable with `--alfonso-confirm-trend-structure`; omit it for baseline behavior.
This is independent of price-structure invalidation and the older structural
agreement switches.

Establishing or reversing a trend now requires both the existing accomplishment
evidence and two confirmed non-continuation zone peaks and two zone valleys.
Both series must strictly rise for Uptrend or strictly fall for Downtrend.
Equal levels and missing swings do not count as confirmation. These are the
detector's zone-derived swings, not arbitrary visual price pivots. Only zone
confirmation events already received on closed candles contribute; future
confirmations do not change earlier decisions.

Opposing elimination evidence without matching structure can invalidate the
old trend, but cannot establish the opposite trend yet. This gate does not
itself continuously invalidate an existing trend or add countertrend entries.
Stops and targets remain unchanged. It can delay or remove trades, so it is an
opt-in experiment, not a claim of improved profitability.

Verification: 135 Alfonso regression tests passed, including ten new cases for
this gate with other experimental switches off. BacktestRunner built without
warnings or errors. Full multi-market performance replays remain separate.
