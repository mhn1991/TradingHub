#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"

dotnet restore TradingHub.slnx
dotnet build TradingHub.slnx -c Release --no-restore
dotnet test TradingHub.slnx -c Release --no-build

cd Dashboard
npm ci
npm run typecheck
npm run build
npm run data:validate
