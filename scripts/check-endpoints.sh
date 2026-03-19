#!/bin/bash
# Quick post-deploy endpoint check (no session required for health endpoints)
# Usage: ./scripts/check-endpoints.sh [VM_IP]
#   VM_IP defaults to terraform output vm_public_ip if available, else localhost

set -euo pipefail

IP="${1:-}"
if [ -z "$IP" ]; then
  # Try terraform output
  IP=$(cd "$(dirname "$0")/../terraform-navalansible" 2>/dev/null && terraform output -raw vm_public_ip 2>/dev/null || echo "")
fi
if [ -z "$IP" ]; then
  IP="localhost"
fi

BASE="https://$IP"
[ "$IP" = "localhost" ] && BASE="http://localhost:5000"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
PASS=0; FAIL=0; WARN=0

check() {
  local desc="$1" url="$2" expect="${3:-200}"
  local code body
  code=$(curl -sk -o /tmp/check-ep-out -w "%{http_code}" --max-time 10 "$url" 2>/dev/null || echo "000")
  if [ "$code" = "$expect" ]; then
    echo -e "${GREEN}OK${NC}  $desc ($code)"
    PASS=$((PASS + 1))
  elif [ "$code" = "000" ]; then
    echo -e "${RED}ERR${NC} $desc - connection failed"
    FAIL=$((FAIL + 1))
  else
    echo -e "${RED}FAIL${NC} $desc - expected $expect, got $code"
    # Show response body for failures
    body=$(cat /tmp/check-ep-out 2>/dev/null | head -c 200)
    [ -n "$body" ] && echo "      $body"
    FAIL=$((FAIL + 1))
  fi
}

echo "=== Naval Archive Quick Endpoint Check ==="
echo "Target: $BASE"
echo ""

# 1. Health (no session, no DB)
if [ "$IP" = "localhost" ]; then
  check "API /health (lightweight)" "$BASE/health"
else
  check "Node /health (web)" "$BASE/health"
fi

# 2. API health with DB check (public, no session)
check "API /api/health (DB check)" "$BASE/api/health"

# 3. Get session cookie for protected endpoints
if [ "$IP" != "localhost" ]; then
  echo ""
  echo "--- Obtaining session cookie ---"
  COOKIE=$(curl -sk -c - "$BASE/" -o /dev/null 2>/dev/null | grep AspNetCore.Session | awk '{print $NF}')
  if [ -n "$COOKIE" ]; then
    echo -e "${GREEN}OK${NC}  Session cookie obtained"
    COOKIE_OPT="-b .AspNetCore.Session=$COOKIE"
  else
    echo -e "${YELLOW}WARN${NC} No session cookie (session gate may be off or request failed)"
    COOKIE_OPT=""
    WARN=$((WARN + 1))
  fi
  API="$BASE/api"
else
  COOKIE_OPT=""
  API="$BASE/api"
fi

echo ""
echo "--- Data endpoints ---"
check "GET /api/ships"     "$(echo $API)/ships?page=1&pageSize=1"
check "GET /api/stats"     "$(echo $API)/stats"
check "GET /api/classes"   "$(echo $API)/classes"
check "GET /api/captains"  "$(echo $API)/captains"
check "GET /api/trace"     "$(echo $API)/trace"
check "GET /api/timeline"  "$(echo $API)/timeline"

if [ "$IP" != "localhost" ]; then
  echo ""
  echo "--- Frontend pages ---"
  for path in "/" "/fleet" "/trace" "/stats" "/login"; do
    code=$(curl -sk -o /dev/null -w "%{http_code}" --max-time 10 $COOKIE_OPT "$BASE$path" 2>/dev/null || echo "000")
    if [ "$code" = "200" ]; then
      echo -e "${GREEN}OK${NC}  GET $path ($code)"
      PASS=$((PASS + 1))
    else
      echo -e "${RED}FAIL${NC} GET $path - got $code"
      FAIL=$((FAIL + 1))
    fi
  done
fi

echo ""
echo "=== Results: ${GREEN}$PASS passed${NC}, ${RED}$FAIL failed${NC}, ${YELLOW}$WARN warnings${NC} ==="
[ "$FAIL" -gt 0 ] && exit 1 || exit 0
