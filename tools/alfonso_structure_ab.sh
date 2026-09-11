#!/bin/bash
# 3.64 A/B: trend latched (A) vs module 5 structural condition maintained (B).
# Reproduces the PROJECT_STATE.md 3.64 A/B. Output root is overridable: ALFONSO_AB_OUT=... ./tools/alfonso_structure_ab.sh
# Requires a warm OANDA candle cache for 2025-11-24..2026-07-23 and an explicit
# `dotnet build BacktestRunner -c Release` beforehand (see 3.50 on stale Release binaries).
# Same window and flags as the 3.40-3.43 / 3.50 runs; one binary, one flag differing.
cd "$(dirname "$0")/.." || exit 1
OUT=${ALFONSO_AB_OUT:-/mnt/storage/scratch/alfonso/m5ab}
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

# Cache is warm from the 3.50-3.52 runs over this exact window, so both arms can go in parallel.
for tag in "${!INST[@]}"; do
  dotnet run --project BacktestRunner -c Release --no-build -- \
    --instrument "${INST[$tag]}" ${QRATE[$tag]:-} $WIN $COMMON \
    --alfonso-candidate-log $OUT/a-$tag.csv --output $OUT/a-$tag \
    > $OUT/a-$tag.log 2>&1 &
  sleep 1
done
wait
echo "ARM A (latched, the current default) COMPLETE"

for tag in "${!INST[@]}"; do
  dotnet run --project BacktestRunner -c Release --no-build -- \
    --instrument "${INST[$tag]}" ${QRATE[$tag]:-} $WIN $COMMON \
    --alfonso-maintain-structure \
    --alfonso-candidate-log $OUT/b-$tag.csv --output $OUT/b-$tag \
    > $OUT/b-$tag.log 2>&1 &
  sleep 1
done
wait
echo "ARM B (structure maintained) COMPLETE"
