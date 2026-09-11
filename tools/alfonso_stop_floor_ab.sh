#!/bin/bash
# 3.68 A/B: no stop floor (A) vs a floor of 0.25 (B) and 0.50 (C) ATR of the TOP timeframe.
#
# Reproduces the PROJECT_STATE.md 3.68 A/B. Output root is overridable:
#   ALFONSO_AB_OUT=... ./tools/alfonso_stop_floor_ab.sh
# Requires a warm OANDA candle cache for 2025-11-24..2026-07-23 and an explicit
# `dotnet build BacktestRunner -c Release` beforehand (see 3.50 on stale Release binaries).
#
# The question: module 8 gives the TOP timeframe the direction while module 10 sizes the stop from
# the zone, which on a drilled-down entry is far smaller. Measured over 135 trades, the median stop
# was 0.21 of a 4h ATR and 93% sat under half of one (3.66). Module 10's fourth entry option is the
# book's own guard - "use the stop padding you would use for the bigger timeframe imbalance" - and
# 3.61 records it as unbuilt; this is its cruder ATR-shaped cousin.
#
# Registered before the runs: trade count should fall by roughly half at 0.25 and ~90% at 0.50.
# On avgR there is no prior - the overshoot evidence (90% of stops exit beyond themselves, 60% of
# stopped trades later print the target) argues the floor helps, while four consecutive A/Bs moving
# toward the book argue it will not.
set -u
cd "$(dirname "$0")/.." || exit 1
OUT=${ALFONSO_AB_OUT:-/mnt/storage/scratch/alfonso/m7ab}
mkdir -p "$OUT"

WIN="--from 2025-11-24 --to 2026-07-23"
COMMON="--execution-interval 1m --precision-mode fast --analysis-intervals 1m,15m,1h,4h
  --strategies alfonso --alfonso-ignore-control --alfonso-profit-margin 0
  --improved-trailing-mode disabled --improved-disable-mechanical-protection
  --improved-no-scale-out --improved-no-profit-floor --improved-no-giveback
  --improved-no-stagnation-reduction --improved-no-structure-reduction
  --improved-no-momentum-reduction --improved-no-volatility-reduction"

declare -A INST=(
  [gold]="METAL:XAU/USD"   [silver]="METAL:XAG/USD"
  [nas100]="CFD:NAS100/USD" [us30]="CFD:US30/USD"
  [eurusd]="FX:EUR/USD"    [gbpjpy]="FX:GBP/JPY"
)
declare -A QRATE=( [gbpjpy]="--quote-rate JPY=0.006322" )

# One arm per call: prefix, then any extra flags. Kept as a function so no arm can end up with a
# truncated command line - a stray blank line inside a backslash continuation silently dropped a
# whole arm once already.
run_arm() {
  local arm="$1"; shift
  for tag in "${!INST[@]}"; do
    # shellcheck disable=SC2086
    dotnet run --project BacktestRunner -c Release --no-build -- \
      --instrument "${INST[$tag]}" ${QRATE[$tag]:-} $WIN $COMMON "$@" \
      --alfonso-candidate-log "$OUT/$arm-$tag.csv" --output "$OUT/$arm-$tag" \
      > "$OUT/$arm-$tag.log" 2>&1 &
    sleep 1
  done
  wait
  echo "ARM ${arm^^} COMPLETE"
}

run_arm a
run_arm b --alfonso-min-stop-top-atr 0.25
run_arm c --alfonso-min-stop-top-atr 0.50
echo "ALL ARMS COMPLETE"
