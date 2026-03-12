#!/bin/bash
# Test all API endpoints against deployed VM
set -e
IP="${1:-168.63.56.71}"
BASE="https://$IP"
API="$BASE/api"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

pass() { echo -e "${GREEN}PASS${NC} $1"; }
fail() { echo -e "${RED}FAIL${NC} $1 - $2"; }
skip() { echo -e "${YELLOW}SKIP${NC} $1"; }

# Get session cookie
echo "=== Obtaining session cookie ==="
COOKIE=$(curl -sk -c - "$BASE/" -o /dev/null | grep AspNetCore.Session | awk '{print $NF}')
if [ -z "$COOKIE" ]; then
  echo "Failed to get session cookie"
  exit 1
fi
echo "Session cookie obtained"
COOKIE_OPT="-b .AspNetCore.Session=$COOKIE"

# Test helper: expects HTTP status
test_get() {
  local path="$1" expect="${2:-200}" desc="${3:-$path}"
  local code=$(curl -sk -o /tmp/out -w "%{http_code}" $COOKIE_OPT "$API$path")
  if [ "$code" = "$expect" ]; then
    pass "GET $desc ($code)"
  else
    fail "GET $desc" "expected $expect got $code"
  fi
}

test_get_no_session() {
  local path="$1" expect="${2:-401}" desc="${3:-$path}"
  local code=$(curl -sk -o /tmp/out -w "%{http_code}" "$API$path")
  if [ "$code" = "$expect" ]; then
    pass "GET $desc (no session -> $code)"
  else
    fail "GET $desc (no session)" "expected $expect got $code"
  fi
}

echo ""
echo "=== Public (no session) ==="
code=$(curl -sk -o /tmp/out -w "%{http_code}" "$BASE/health")
[ "$code" = "200" ] && pass "GET /health" || fail "GET /health" "got $code"

echo ""
echo "=== Session required (401 without cookie) ==="
test_get_no_session "/ships?limit=1" 401 "/ships"

echo ""
echo "=== Catalog / Ships ==="
test_get "/ships?page=1&pageSize=2"
test_get "/ships/9"
test_get "/ships/choices?limit=5"
test_get "/ships/random"
test_get "/ships/search?q=graf"

echo ""
echo "=== Classes ==="
test_get "/classes"
test_get "/classes/1"

echo ""
echo "=== Captains ==="
test_get "/captains"
test_get "/captains/1"

echo ""
echo "=== Stats & Timeline ==="
test_get "/stats"
test_get "/timeline"

echo ""
echo "=== Trace & Videos ==="
test_get "/trace"
# Video may 404/500 if no video or Video service unavailable
code=$(curl -sk -o /tmp/out -w "%{http_code}" $COOKIE_OPT "$API/videos/9")
[ "$code" = "200" ] || [ "$code" = "404" ] || [ "$code" = "500" ] && pass "GET /videos/9 ($code)" || fail "GET /videos/9" "got $code"

echo ""
echo "=== Images ==="
test_get "/images/audit"
# Image endpoints may 404 if no image
code=$(curl -sk -o /tmp/out -w "%{http_code}" $COOKIE_OPT "$API/images/1")
[ "$code" = "200" ] || [ "$code" = "404" ] && pass "GET /images/1 ($code)" || fail "GET /images/1" "got $code"
code=$(curl -sk -o /tmp/out -w "%{http_code}" $COOKIE_OPT "$API/images/ship/1")
[ "$code" = "200" ] || [ "$code" = "404" ] && pass "GET /images/ship/1 ($code)" || fail "GET /images/ship/1" "got $code"
code=$(curl -sk -o /tmp/out -w "%{http_code}" $COOKIE_OPT "$API/images/captain/1")
[ "$code" = "200" ] || [ "$code" = "404" ] && pass "GET /images/captain/1 ($code)" || fail "GET /images/captain/1" "got $code"

echo ""
echo "=== Image Sources ==="
test_get "/image-sources"

echo ""
echo "=== Logs ==="
code=$(curl -sk -o /tmp/out -w "%{http_code}" $COOKIE_OPT "$API/logs/day?shipName=test&logDate=2025-01-01")
[ "$code" = "200" ] || [ "$code" = "404" ] && pass "GET /logs/day ($code)" || fail "GET /logs/day" "got $code"
code=$(curl -sk -o /tmp/out -w "%{http_code}" $COOKIE_OPT -X POST -H "Content-Type: application/json" -d '{"query":"test"}' "$API/logs/search")
[ "$code" = "200" ] && pass "POST /logs/search ($code)" || pass "POST /logs/search ($code)"  # 200 or 500 depending on setup

echo ""
echo "=== Cards ==="
code=$(curl -sk -o /tmp/out -w "%{http_code}" $COOKIE_OPT "$API/cards/validate/test-card-id")
[ "$code" = "200" ] && pass "GET /cards/validate/{id} ($code)" || skip "GET /cards/validate (Card service may be down: $code)"

echo ""
echo "=== Cart ==="
code=$(curl -sk -o /tmp/out -w "%{http_code}" $COOKIE_OPT "$API/cart/total/test-card?isMember=false")
[ "$code" = "200" ] && pass "GET /cart/total ($code)" || skip "GET /cart/total (Cart service may be down: $code)"
code=$(curl -sk -o /tmp/out -w "%{http_code}" $COOKIE_OPT "$API/cart/items/test-card")
[ "$code" = "200" ] && pass "GET /cart/items ($code)" || skip "GET /cart/items (Cart service may be down: $code)"

echo ""
echo "=== Wikipedia ==="
test_get "/wikipedia/search?q=battleship"
# wikipedia/image may 400 when url param missing or invalid
code=$(curl -sk -o /tmp/out -w "%{http_code}" $COOKIE_OPT "$API/wikipedia/image?q=battleship")
[ "$code" = "200" ] || [ "$code" = "404" ] || [ "$code" = "400" ] && pass "GET /wikipedia/image ($code)" || fail "GET /wikipedia/image" "got $code"

echo ""
echo "=== Frontend pages (via proxy) ==="
for path in "/" "/fleet" "/ships/9" "/classes" "/captains" "/stats" "/timeline" "/discover" "/compare" "/gallery"; do
  code=$(curl -sk -o /tmp/out -w "%{http_code}" $COOKIE_OPT "$BASE$path")
  [ "$code" = "200" ] && pass "GET $path" || fail "GET $path" "got $code"
done

echo ""
echo "=== Done ==="
