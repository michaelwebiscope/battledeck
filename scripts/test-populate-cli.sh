#!/bin/bash
# Test image populate and terminal progress via CLI.
# Usage: ./scripts/test-populate-cli.sh [test-terminal|populate]
#   test-terminal - Test the fake terminal (emits events over ~12s)
#   populate      - Run real populate with polling (no sync)
#
# Requires: API at localhost:5000, Web at localhost:3000, jq
# Ensure NavalArchive.Web is running (npm start) with latest code.

set -e
cd "$(dirname "$0")/.."
WEB_URL="${WEB_URL:-http://localhost:3000}"
API_URL="${API_URL:-http://localhost:5000}"
MODE="${1:-test-terminal}"

echo "=== Populate CLI Test ==="
echo "Web: $WEB_URL | API: $API_URL"
echo ""

# Preflight: ensure endpoint returns JSON
check_json() {
  local resp
  resp=$(curl -s -w '\n%{http_code}' "$@")
  local body="${resp%$'\n'*}"
  local code="${resp##*$'\n'}"
  if [ "$code" != "200" ]; then
    echo "   FAIL: HTTP $code"
    echo "$body" | head -5
    exit 1
  fi
  if ! echo "$body" | jq -e . >/dev/null 2>&1; then
    echo "   FAIL: Response is not JSON (ensure server has latest routes)"
    echo "$body" | head -3
    exit 1
  fi
  echo "$body"
}

if [ "$MODE" = "test-terminal" ]; then
  echo "1. Starting test terminal (fake progress over ~12s)..."
  BODY=$(check_json -X POST "$WEB_URL/admin/images/populate/test-terminal" -H "Content-Type: application/json" -d '{}')
  JOB=$(echo "$BODY" | jq -r '.jobId')
  if [ -z "$JOB" ] || [ "$JOB" = "null" ]; then
    echo "   FAIL: No jobId in response"
    exit 1
  fi
  echo "   Job: $JOB"
  echo ""
  echo "2. Polling (every 1.2s)..."
  for i in $(seq 1 15); do
    ST=$(curl -s "$WEB_URL/admin/images/populate/status/$JOB")
    EVENTS=$(echo "$ST" | jq -r '.events | length')
    DONE=$(echo "$ST" | jq -r '.done')
    echo "   Poll $i: $EVENTS events, done=$DONE"
    [ "$DONE" = "true" ] && break
    sleep 1
  done
  echo ""
  echo "3. Final output (formatted):"
  echo "$ST" | jq -r '.events[] | (.type // .Type) as $t | (.message // .data // .Data) as $v | "   \($t): \(if $v | type == "object" then (($v.name // $v.id) | tostring) else ($v // "") end)"' 2>/dev/null || echo "$ST" | jq .
  echo ""
  echo "OK: Test terminal works. In browser: click 'Test Terminal' to see live updates."
  exit 0
fi

if [ "$MODE" = "populate" ]; then
  echo "1. Audit before..."
  curl -s "$API_URL/api/images/audit" | jq -r '"   Ships: \(.ships.withImageData)/\(.ships.total) cached, Captains: \(.captains.withImageData)/\(.captains.total) cached"'
  echo ""
  echo "2. Starting populate (polling, no sync)..."
  BODY=$(check_json -X POST "$WEB_URL/admin/images/populate" -H "Content-Type: application/json" -d '{"runSyncFirst":false,"usePolling":true}')
  JOB=$(echo "$BODY" | jq -r '.jobId')
  if [ -z "$JOB" ] || [ "$JOB" = "null" ]; then
    echo "   FAIL: No jobId (ensure usePolling:true in request)"
    echo "   Raw: $BODY"
    exit 1
  fi
  echo "   Job: $JOB"
  echo ""
  echo "3. Polling until done..."
  while true; do
    ST=$(curl -s "$WEB_URL/admin/images/populate/status/$JOB")
    EVENTS=$(echo "$ST" | jq -r '.events | length')
    DONE=$(echo "$ST" | jq -r '.done')
    echo "   $EVENTS events, done=$DONE"
    [ "$DONE" = "true" ] && break
    sleep 2
  done
  echo ""
  echo "4. Audit after..."
  curl -s "$API_URL/api/images/audit" | jq -r '"   Ships: \(.ships.withImageData)/\(.ships.total) cached, Captains: \(.captains.withImageData)/\(.captains.total) cached"'
  echo ""
  echo "OK: Populate complete."
  exit 0
fi

echo "Usage: $0 [test-terminal|populate]"
exit 1
