#!/usr/bin/env bash
# Single-factor Silver test against the completed both-trend-fixes / zone-target control.
set -euo pipefail
cd "$(dirname "$0")/.."
OUT=${1:?Provide a new output directory}
shift
if [[ -e "$OUT" ]]; then
  echo "Refusing to overwrite existing results: $OUT" >&2; exit 1
fi
mkdir -p "$OUT"
OUT=$(realpath "$OUT")
git rev-parse HEAD > "$OUT/code-revision.txt"
git diff --stat > "$OUT/working-tree.txt"
dotnet BacktestRunner/bin/Release/net10.0/BacktestRunner.dll \
  --instrument METAL:XAG/USD --from 2025-11-24 --to 2026-07-23 --warmup-days 21 \
  --execution-interval 1m --precision-mode fast --analysis-intervals 1m,5m,15m,1h,4h \
  --strategies alfonso --alfonso-entry-policy lower-aligned \
  --alfonso-invalidate-price-structure --alfonso-confirm-trend-structure \
  --alfonso-ignore-control --alfonso-profit-margin 0 --minimum-rr 0 \
  --alfonso-opposing-zone-target --alfonso-structural-stop --alfonso-stop-lookback 48 \
  --no-capture-market-replay \
  --improved-trailing-mode disabled --improved-disable-mechanical-protection \
  --improved-no-scale-out --improved-no-profit-floor --improved-no-giveback \
  --improved-no-stagnation-reduction --improved-no-structure-reduction \
  --improved-no-momentum-reduction --improved-no-volatility-reduction \
  --alfonso-candidate-log "$OUT/lower-aligned.csv" \
  "$@" \
  --output "$OUT/lower-aligned" > "$OUT/lower-aligned.log" 2>&1
echo "Completed lower-aligned"
if ! python3 tools/alfonso_publish_completed.py "$OUT"; then
  echo "Replay completed, but dashboard publication failed. Results are safe in: $OUT" >&2
  echo "Retry publication without rerunning the test: python3 tools/alfonso_publish_completed.py '$OUT'" >&2
  exit 1
fi
