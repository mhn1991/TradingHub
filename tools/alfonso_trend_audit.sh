#!/usr/bin/env bash
# One baseline backtest per instrument; three read-only trend shadows share its exact closed-bar feed.
# Build first: dotnet build BacktestRunner -c Release
# Usage: bash tools/alfonso_trend_audit.sh /absolute/new/output-directory [gold silver ...]
set -euo pipefail
cd "$(dirname "$0")/.."
OUT=${1:?Provide a new output directory}; shift
if [[ -e "$OUT" ]]; then
  echo "Refusing to overwrite existing audit directory: $OUT" >&2; exit 1
fi
mkdir -p "$OUT"
OUT=$(realpath "$OUT")
git rev-parse HEAD > "$OUT/code-revision.txt"
git diff --stat > "$OUT/working-tree.txt"
if [[ $# == 0 ]]; then set -- gold silver nas100 us30 gbpjpy eurusd; fi
declare -A INST=( [gold]='METAL:XAU/USD' [silver]='METAL:XAG/USD'
  [nas100]='CFD:NAS100/USD' [us30]='CFD:US30/USD' [gbpjpy]='FX:GBP/JPY' [eurusd]='FX:EUR/USD' )
COMMON=(--from 2025-11-24 --to 2026-07-23 --warmup-days 21
  --execution-interval 1m --precision-mode fast --analysis-intervals 1m,15m,1h,4h
  --strategies alfonso --alfonso-entry-policy core --alfonso-ignore-control --alfonso-profit-margin 0
  --no-capture-market-replay
  --improved-trailing-mode disabled --improved-disable-mechanical-protection
  --improved-no-scale-out --improved-no-profit-floor --improved-no-giveback
  --improved-no-stagnation-reduction --improved-no-structure-reduction
  --improved-no-momentum-reduction --improved-no-volatility-reduction)
declare -a PIDS=() TAGS=()
for tag in "$@"; do
  [[ -v INST[$tag] ]] || { echo "Unknown instrument: $tag" >&2; exit 1; }
done
for tag in "$@"; do
  EXTRA=()
  if [[ "$tag" == gbpjpy ]]; then EXTRA=(--quote-rate JPY=0.006322); fi
  dotnet BacktestRunner/bin/Release/net10.0/BacktestRunner.dll \
    --instrument "${INST[$tag]}" "${COMMON[@]}" "${EXTRA[@]}" \
    --alfonso-trend-audit "$OUT/$tag.jsonl" --output "$OUT/$tag" > "$OUT/$tag.log" 2>&1 &
  PIDS+=("$!"); TAGS+=("$tag")
  echo "Started $tag (PID $!)"
done
failed=0
for i in "${!PIDS[@]}"; do
  if wait "${PIDS[$i]}"; then
    echo "Completed ${TAGS[$i]}"
  else
    echo "FAILED ${TAGS[$i]}: inspect $OUT/${TAGS[$i]}.log" >&2
    failed=1
  fi
done
exit "$failed"
