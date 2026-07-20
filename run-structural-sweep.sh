#!/usr/bin/env bash
# Parallel parameter sweep for the structural-confluence agent's playbook/geometry options.
# Replays real cached candles through the exact production evidence pipeline (no broker/fills/
# trade-management), so it's fast and answers "how many trade candidates would this configuration
# produce, and why do the rest fail" - not "what would the P&L have been."
#
# Usage:
#   ./run-structural-sweep.sh
#   ./run-structural-sweep.sh --instrument "FX:EUR/USD" --from 2026-05-11T00:00:00Z --to 2026-06-29T00:00:00Z
#   ./run-structural-sweep.sh --candles /path/to/cached/FX_EUR_USD_1m_....jsonl.gz
#
# See StructuralParameterSweep/Program.cs for the full flag list (--context/--setup/--trigger/
# --additional-context) and to edit the variant matrix itself.

set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

dotnet build StructuralParameterSweep/StructuralParameterSweep.csproj -c Release --nologo -v quiet
dotnet run --project StructuralParameterSweep/StructuralParameterSweep.csproj -c Release --no-build -- "$@"
