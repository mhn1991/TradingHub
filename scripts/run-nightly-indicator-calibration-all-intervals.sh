#!/usr/bin/env bash
# Launch indicator calibration for 5m and 15m in parallel.
# No runtime budget: each interval runs to Completed/Failed/Cancelled (full search plan).
#
# 5m and 15m are the two execution (== trigger/decision) intervals worth calibrating for this
# strategy - 1m has no decision role (the strategy never looks at 1m bars), so there's no reason to
# annotate/search at that granularity. run-nightly-indicator-calibration.sh derives matching
# setup/context intervals for whichever execution interval it's given (see its own
# SETUP_INTERVAL/CONTEXT_INTERVAL case statement), so both entries here run against a valid,
# internally-consistent timeframe stack rather than the old fixed 5m/15m/1h shape.
#
# Usage:
#   scripts/run-nightly-indicator-calibration-all-intervals.sh <strategy> <instrument>
#
# Example (from repo root; keep a launcher log so nohup failures are visible):
#   nohup scripts/run-nightly-indicator-calibration-all-intervals.sh \
#     indicator-confluence "FX:EUR/USD" \
#     >> .cache/nightly-calibration-logs/all-intervals.nohup.log 2>&1 &
#
# Each interval runs via run-nightly-indicator-calibration.sh (own state/lock/log).
# Concurrent builds are serialized by that script's shared flock.
set -euo pipefail

STRATEGY="${1:?Usage: $0 <indicator-confluence|liquidity-break-retest> <instrument>}"
INSTRUMENT="${2:?Usage: $0 <indicator-confluence|liquidity-break-retest> <instrument>}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

RUNNER="$SCRIPT_DIR/run-nightly-indicator-calibration.sh"
INTERVALS=(5m 15m)
LOG_DIR="$REPO_ROOT/.cache/nightly-calibration-logs"
mkdir -p "$LOG_DIR"

SAFE_INSTRUMENT="$(printf '%s' "$INSTRUMENT" | tr -c 'A-Za-z0-9' '_')"
TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
LAUNCHER_LOG="$LOG_DIR/${STRATEGY}_${SAFE_INSTRUMENT}_all-intervals_${TIMESTAMP}.log"

# Always mirror launcher output to a log file (even if caller redirects nohup to /dev/null).
exec > >(tee -a "$LAUNCHER_LOG") 2>&1

if [[ ! -x "$RUNNER" ]]; then
    echo "Runner not found or not executable: $RUNNER" >&2
    exit 1
fi

echo "=== All-intervals launcher @ $TIMESTAMP ==="
echo "cwd: $REPO_ROOT"
echo "Starting parallel calibrations for $STRATEGY / $INSTRUMENT (no runtime budget - run to completion)"
echo "Intervals: ${INTERVALS[*]}"
echo "Launcher log: $LAUNCHER_LOG"
echo

PIDS=()
for interval in "${INTERVALS[@]}"; do
    echo "  launching $interval ..."
    "$RUNNER" "$STRATEGY" "$INSTRUMENT" "$interval" &
    PIDS+=($!)
done

echo
echo "PIDs: ${PIDS[*]}"
echo "Per-interval logs: $LOG_DIR/"
echo "Waiting for all intervals to finish (this can take many hours/days)..."
echo

FAIL=0
for i in "${!PIDS[@]}"; do
    pid="${PIDS[$i]}"
    interval="${INTERVALS[$i]}"
    if wait "$pid"; then
        echo "  $interval (pid $pid): ok"
    else
        status=$?
        echo "  $interval (pid $pid): exited $status" >&2
        FAIL=1
    fi
done

if [[ "$FAIL" -ne 0 ]]; then
    echo "One or more interval runs failed." >&2
    exit 1
fi

echo "All intervals finished successfully."
