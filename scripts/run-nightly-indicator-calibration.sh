#!/usr/bin/env bash
# Indicator-calibration runner - runs to actual completion, no artificial time box.
#
# Usage:
#   scripts/run-nightly-indicator-calibration.sh <strategy> <instrument> [execution-interval]
#
# Example (kick off once, let it run for however long it takes - hours to days):
#   nohup scripts/run-nightly-indicator-calibration.sh indicator-confluence "FX:EUR/USD" 1m \
#     >> .cache/nightly-calibration-logs/1m.nohup.log 2>&1 &
#
# Behavior:
#   - Tracks one calibration run per (strategy, instrument, execution-interval) in a small state
#     file under .cache/nightly-calibration-state/. Re-running this script for the same key resumes
#     that run if it is not yet finished, rather than starting a new one.
#   - Runs the CLI's own poll loop to a genuine terminal state (Completed/Failed/Cancelled) with NO
#     external timeout - a full 6-parameter search over real data is measured at 9-20+ hours (see
#     EvaluationBudgetEstimator's measured constants), so this call is expected to block for a long
#     time. If the process is killed anyway (reboot, OOM, manual Ctrl+C), that is still safe: the
#     ledger is durably saved before any evaluation work starts
#     (IndicatorCalibrationApplicationService.StartAsync), and already-completed evaluations survive
#     in the persistent candidate cache regardless of how the process ends - re-running this script
#     resumes from there rather than losing prior work.
#   - No wall-clock budget and no search-scope shrink for a maintenance window: generate-request is
#     given an effectively unlimited --hard-runtime-limit-hours so OverflowPolicy=ReduceRefinement
#     does not drop refinement / shrink the plan. The process still only exits when the calibration
#     reaches Completed/Failed/Cancelled (or is killed externally).
#   - A file lock (flock) keyed to (strategy, instrument, execution-interval) prevents two
#     invocations for the same key from running concurrently (e.g. a re-triggered/duplicate call
#     while a previous one is still going) - safe to run different intervals for the same instrument
#     in parallel (each gets its own key/lock), just not the same interval twice at once.
#   - A separate shared flock around the one-time `dotnet build` serializes concurrent builds of
#     BacktestRunner (parallel keys would otherwise race on shared obj/bin). The lock is held only
#     for the build, then released so calibrations run independently.
#   - Never auto-approves anything: a completed run only produces a PendingReview artifact. Use
#     `indicator-calibration pending` / `review --artifact-id <guid>` / `approve ...` separately.
#   - If a prior run for this key already reached Failed or Cancelled, this script stops and reports
#     it rather than silently starting a fresh run - a repeated failure usually means something is
#     actually wrong and auto-retrying forever would just waste compute. Delete the state file
#     (.cache/nightly-calibration-state/<key>.json) to deliberately start over.
set -euo pipefail

STRATEGY="${1:?Usage: $0 <indicator-confluence|liquidity-break-retest> <instrument> [execution-interval=1m]}"
INSTRUMENT="${2:?Usage: $0 <indicator-confluence|liquidity-break-retest> <instrument> [execution-interval=1m]}"
EXECUTION_INTERVAL="${3:-1m}"

# Ceiling for the estimator only (must be > 0). Large enough that overflow never shrinks the plan.
# Not a process kill timer - the script waits for a real terminal state.
UNLIMITED_HARD_RUNTIME_LIMIT_HOURS=876000

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

STATE_DIR="$REPO_ROOT/.cache/nightly-calibration-state"
LOG_DIR="$REPO_ROOT/.cache/nightly-calibration-logs"
BUILD_LOCK_FILE="$REPO_ROOT/.cache/dotnet-build.lock"
mkdir -p "$STATE_DIR" "$LOG_DIR"

SAFE_INSTRUMENT="$(printf '%s' "$INSTRUMENT" | tr -c 'A-Za-z0-9' '_')"
KEY="${STRATEGY}_${SAFE_INSTRUMENT}_${EXECUTION_INTERVAL}"
STATE_FILE="$STATE_DIR/${KEY}.json"
REQUEST_FILE="$STATE_DIR/${KEY}_request.json"
LOCK_FILE="$STATE_DIR/${KEY}.lock"
TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
LOG_FILE="$LOG_DIR/${KEY}_${TIMESTAMP}.log"

exec 9>"$LOCK_FILE"
if ! flock -n 9; then
    echo "Another run for key '$KEY' is already in progress (lock: $LOCK_FILE) - exiting." >&2
    exit 1
fi

# MSBuild node-reuse workers outlive `dotnet build`/`dotnet run` and inherit open fds. If they
# inherit the flock fd they pin the exclusive lock forever (and other intervals hang). Disable
# reuse and close lock fds on child processes so only this script holds them.
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1

run_cli() {
    # Close key-lock fd 9 in the child so short-lived CLI helpers / MSBuild helpers cannot pin it.
    dotnet run --project "$REPO_ROOT/BacktestRunner" --no-build -- indicator-calibration "$@" 9>&-
}

status_state() {
    run_cli status --id "$1" | jq -r '.summary.state'
}

print_final_status() {
    run_cli status --id "$1" | jq -r '.summary | "state=\(.state) outcome=\(.outcome // "n/a") artifactId=\(.artifactId // "n/a")"'
}

build_backtest_runner() {
    # Hold build lock only in this subshell; close fd 200 in the build process so MSBuild
    # workers cannot inherit and pin it after the build exits.
    (
        flock 200 || exit 1
        dotnet build "$REPO_ROOT/BacktestRunner" -v quiet 200>&- 9>&-
    ) 200>"$BUILD_LOCK_FILE"
}

{
    echo "=== Indicator calibration: $STRATEGY / $INSTRUMENT / $EXECUTION_INTERVAL @ $TIMESTAMP ==="
    echo "Building BacktestRunner once (subsequent invocations this run use --no-build)..."
    # Serialize concurrent builds: parallel invocations of this script (different keys) otherwise
    # race on BacktestRunner's shared obj/bin and can fail flakily.
    build_backtest_runner

    CALIBRATION_ID=""
    if [[ -f "$STATE_FILE" ]]; then
        CALIBRATION_ID="$(jq -r '.calibrationId' "$STATE_FILE")"
        EXISTING_STATE="$(status_state "$CALIBRATION_ID")"
        echo "Found existing calibration run $CALIBRATION_ID for this key - current state: $EXISTING_STATE"

        case "$EXISTING_STATE" in
            Completed)
                echo "Already Completed - nothing to do. Run 'indicator-calibration pending'/'review' to see its outcome."
                print_final_status "$CALIBRATION_ID"
                exit 0
                ;;
            Failed|Cancelled)
                echo "This run previously ended $EXISTING_STATE - not auto-restarting (could mask a real problem)."
                echo "Inspect with 'indicator-calibration status --id $CALIBRATION_ID', then delete $STATE_FILE to start fresh."
                print_final_status "$CALIBRATION_ID"
                exit 1
                ;;
            *)
                echo "Resuming - this will block until the run reaches a terminal state (no time limit)."
                dotnet run --project "$REPO_ROOT/BacktestRunner" --no-build -- \
                    indicator-calibration resume --id "$CALIBRATION_ID" 9>&- || true
                ;;
        esac
    else
        echo "Generating a new calibration request (full search - no runtime budget / no scope shrink)."
        run_cli generate-request \
            --strategy "$STRATEGY" \
            --instrument "$INSTRUMENT" \
            --execution-interval "$EXECUTION_INTERVAL" \
            --hard-runtime-limit-hours "$UNLIMITED_HARD_RUNTIME_LIMIT_HOURS" \
            --output "$REQUEST_FILE"

        echo "Starting - this will block until the run reaches a terminal state (no time limit)."
        # --accept-budget acknowledges the estimate; with the unlimited ceiling above, the plan is
        # not reduced to fit a maintenance window.
        dotnet run --project "$REPO_ROOT/BacktestRunner" --no-build -- \
            indicator-calibration start --request "$REQUEST_FILE" --accept-budget \
            9>&- \
            | tee "$STATE_DIR/${KEY}_last_start.log" || true

        NEW_ID="$(grep -oE 'Started calibration run [0-9a-f]+' "$STATE_DIR/${KEY}_last_start.log" | awk '{print $NF}' || true)"
        if [[ -z "$NEW_ID" ]]; then
            echo "Could not determine the new calibration id from CLI output - nothing to persist. Check the log above."
            exit 1
        fi
        printf '{"calibrationId": "%s"}\n' "$NEW_ID" > "$STATE_FILE"
        CALIBRATION_ID="$NEW_ID"
    fi

    echo "--- Final status for $CALIBRATION_ID ---"
    print_final_status "$CALIBRATION_ID"
    echo "=== Done. Run 'indicator-calibration pending' to see anything awaiting manual review. ==="
} 2>&1 | tee -a "$LOG_FILE"
