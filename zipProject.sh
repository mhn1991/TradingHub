#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_name="$(basename "$project_dir")"
output_file="${1:-$(dirname "$project_dir")/${project_name}_source.zip}"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

rsync -a "$project_dir/" "$staging/$project_name/" \
  --exclude='.git/' \
  --exclude='.idea/' \
  --exclude='.vs/' \
  --exclude='.cache/' \
  --exclude='**/bin/' \
  --exclude='**/obj/' \
  --exclude='**/node_modules/' \
  --exclude='**/dist/' \
  --exclude='**/coverage/' \
  --exclude='**/TestResults/' \
  --exclude='Dashboard/public/data/backtests/***' \
  --exclude='Dashboard/public/data/simulations/***' \
  --include='.env.example' \
  --include='.env.integration.example' \
  --exclude='.env' \
  --exclude='.env.*' \
  --exclude='*.zip' \
  --exclude='*.tmp' \
  --exclude='*~' \
  --exclude='*.log' \
  --exclude='*.json.gz'

mkdir -p \
  "$staging/$project_name/Dashboard/public/data/backtests" \
  "$staging/$project_name/Dashboard/public/data/simulations"
touch \
  "$staging/$project_name/Dashboard/public/data/backtests/.gitkeep" \
  "$staging/$project_name/Dashboard/public/data/simulations/.gitkeep"

rm -f "$output_file"
(
  cd "$staging"
  zip -qr "$output_file" "$project_name"
)

echo "Created $output_file"
