#!/usr/bin/env bash
# Four Silver zone-target replays. Reuse the completed fixed-R controls for comparison.
# Build first: dotnet build BacktestRunner -c Release
set -euo pipefail
cd "$(dirname "$0")/.."
OUT=${1:?Provide a new output directory}
dotnet BacktestRunner/bin/Release/net10.0/BacktestRunner.dll \
  --alfonso-opposing-zone-target --minimum-rr 0 --help > /dev/null
if [[ -e "$OUT" ]]; then
  echo "Refusing to overwrite existing results: $OUT" >&2; exit 1
fi
mkdir -p "$OUT"
OUT=$(realpath "$OUT")
git rev-parse HEAD > "$OUT/code-revision.txt"
git diff --stat > "$OUT/working-tree.txt"
COMMON=(--instrument METAL:XAG/USD --from 2025-11-24 --to 2026-07-23 --warmup-days 21
  --execution-interval 1m --precision-mode fast --analysis-intervals 1m,15m,1h,4h
  --strategies alfonso --alfonso-entry-policy core --alfonso-ignore-control --alfonso-profit-margin 0
  --minimum-rr 0 --alfonso-opposing-zone-target --alfonso-structural-stop --alfonso-stop-lookback 48
  --no-capture-market-replay
  --improved-trailing-mode disabled --improved-disable-mechanical-protection
  --improved-no-scale-out --improved-no-profit-floor --improved-no-giveback
  --improved-no-stagnation-reduction --improved-no-structure-reduction
  --improved-no-momentum-reduction --improved-no-volatility-reduction)
declare -a PIDS=() TAGS=()
for variant in baseline price-break confirmed both; do
  EXTRA=()
  if [[ "$variant" == price-break || "$variant" == both ]]; then EXTRA+=(--alfonso-invalidate-price-structure); fi
  if [[ "$variant" == confirmed || "$variant" == both ]]; then EXTRA+=(--alfonso-confirm-trend-structure); fi
  tag="$variant-zone"
  dotnet BacktestRunner/bin/Release/net10.0/BacktestRunner.dll \
    "${COMMON[@]}" "${EXTRA[@]}" --alfonso-candidate-log "$OUT/$tag.csv" \
    --output "$OUT/$tag" > "$OUT/$tag.log" 2>&1 &
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
