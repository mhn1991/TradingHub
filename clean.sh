#!/usr/bin/env bash

set -euo pipefail

echo "Removing bin and obj directories under: $(pwd)"

find . -type d \( -name bin -o -name obj \) -prune -print -exec rm -rf {} +

echo "Cleanup completed."
