#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "== Phase 1 Gate: API smoke =="
"$ROOT/scripts/test-api.sh"

echo ""
echo "== Phase 1 Gate: Dynamic lists DB mode =="
"$ROOT/scripts/test-dynamic-lists-db-mode.sh"

echo ""
echo "== Phase 1 Gate: Startup rollout logs =="
"$ROOT/scripts/test-startup-logs.sh"

echo ""
echo "Phase 1 gate passed."
