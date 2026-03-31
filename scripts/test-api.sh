#!/usr/bin/env bash
# Smoke-test NavalArchive.Api (HTTP only). Does not start the server.
#
# Usage:
#   ./scripts/test-api.sh
#   API_TEST_URL=http://127.0.0.1:5040 ./scripts/test-api.sh
#
# To run the API on a non-default port without launchSettings overriding URLs:
#   ASPNETCORE_ENVIRONMENT=Development dotnet run --project NavalArchive.Api --no-launch-profile --urls http://127.0.0.1:5055

set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE="${API_TEST_URL:-http://127.0.0.1:5000}"

echo "== Build NavalArchive.Api =="
dotnet build "$ROOT/NavalArchive.Api/NavalArchive.Api.csproj" -v minimal

fail() {
  echo "FAIL: $1" >&2
  exit 1
}
skip() {
  echo "SKIP: $1"
}

echo "== GET $BASE/health =="
code=$(curl -sS -o /tmp/naval-api-health.body -w '%{http_code}' "$BASE/health" || true)
[[ "$code" == "200" ]] || fail "/health expected HTTP 200, got $code ($(cat /tmp/naval-api-health.body 2>/dev/null | head -c200))"
grep -q '"status"' /tmp/naval-api-health.body || fail "/health body missing status"
echo "OK ($code)"

echo "== GET $BASE/api/health =="
code=$(curl -sS -o /tmp/naval-api-dbhealth.body -w '%{http_code}' "$BASE/api/health" || true)
[[ "$code" == "200" ]] || fail "/api/health expected HTTP 200, got $code"
echo "OK ($code) $(head -c 120 /tmp/naval-api-dbhealth.body)..."

echo "== GET $BASE/api/ships?page=1&pageSize=2 =="
code=$(curl -sS -g -o /tmp/naval-api-ships.body -w '%{http_code}' "$BASE/api/ships?page=1&pageSize=2" || true)
[[ "$code" == "200" ]] || fail "/api/ships expected HTTP 200, got $code — is the API running at $BASE?"
count=$(python3 -c "
import json
with open('/tmp/naval-api-ships.body') as f:
    d = json.load(f)
items = d.get('items')
assert isinstance(items, list), 'missing .items array'
print(len(items))
") || fail "/api/ships JSON missing .items array"
echo "OK ($code), items count: $count"

echo "== GET $BASE/api/dynamic-lists/diagnostics =="
code=$(curl -sS -o /tmp/naval-api-dlf-diag.body -w '%{http_code}' "$BASE/api/dynamic-lists/diagnostics" || true)
if [[ "$code" == "404" ]]; then
  skip "/api/dynamic-lists/diagnostics not available on running API (restart API to validate this endpoint)"
else
  [[ "$code" == "200" ]] || fail "/api/dynamic-lists/diagnostics expected HTTP 200, got $code"
  python3 - <<'PY' || fail "/api/dynamic-lists/diagnostics JSON missing required fields"
import json
with open('/tmp/naval-api-dlf-diag.body') as f:
    d = json.load(f)
assert isinstance(d.get('mode'), dict), 'missing mode object'
assert isinstance(d.get('counters'), dict), 'missing counters object'
assert isinstance(d.get('fallbackByReason'), dict), 'missing fallbackByReason object'
print("OK (200), diagnostics fields present")
PY
fi

echo "All API smoke checks passed."

if [[ "${INCLUDE_STARTUP_LOG_CHECKS:-false}" == "true" ]]; then
  echo "== Startup log checks =="
  "$ROOT/scripts/test-startup-logs.sh"
fi
