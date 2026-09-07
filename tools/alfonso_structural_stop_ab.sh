#!/usr/bin/env bash
# Build first: dotnet build BacktestRunner -c Release
# Usage: bash tools/alfonso_structural_stop_ab.sh /absolute/new/output-directory [zone structural]
# Silver, original full window and trend/entry settings. 3R arms are isolation controls,
# not proposed target settings. Each independent replay includes risk-based position sizing.
set -euo pipefail
cd "$(dirname "$0")/.."
OUT=${1:?Provide a new output directory}
shift
if [[ $# == 0 ]]; then set -- zone structural; fi
for policy in "$@"; do
  [[ "$policy" == zone || "$policy" == structural ]] || { echo "Unknown stop policy: $policy" >&2; exit 1; }
done
# Exercise the runner's boolean-flag registration before launching any expensive replay.
dotnet BacktestRunner/bin/Release/net10.0/BacktestRunner.dll \
  --alfonso-structural-stop --alfonso-stop-lookback 48 --alfonso-reward 0.75 --help > /dev/null
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
  --minimum-rr 0.75
  --no-capture-market-replay
  --improved-trailing-mode disabled --improved-disable-mechanical-protection
  --improved-no-scale-out --improved-no-profit-floor --improved-no-giveback
  --improved-no-stagnation-reduction --improved-no-structure-reduction
  --improved-no-momentum-reduction --improved-no-volatility-reduction)
declare -a PIDS=() TAGS=()
for policy in "$@"; do
  for reward in 3 0.75 1; do
    tag="$policy-${reward}r"
    EXTRA=(--alfonso-reward "$reward")
    if [[ "$policy" == structural ]]; then
      EXTRA+=(--alfonso-structural-stop --alfonso-stop-lookback 48)
    fi
    dotnet BacktestRunner/bin/Release/net10.0/BacktestRunner.dll \
      "${COMMON[@]}" "${EXTRA[@]}" --output "$OUT/$tag" > "$OUT/$tag.log" 2>&1 &
    PIDS+=("$!"); TAGS+=("$tag")
    echo "Started $tag (PID $!)"
  done
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
