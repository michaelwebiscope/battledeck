#!/bin/bash
# Verify Fleet Roster images work end-to-end.
# Usage: ./scripts/verify-images.sh [WEB_URL] [API_URL]
# Requires: Web and API running, jq, curl

set -e
cd "$(dirname "$0")/.."
WEB_URL="${1:-http://localhost:3000}"
API_URL="${2:-http://localhost:5000}"

echo "=== Image Pipeline Verification ==="
echo "Web: $WEB_URL | API: $API_URL"
echo ""

echo "1. API audit..."
AUDIT=$(curl -s "$API_URL/api/images/audit" | jq -r '"   Ships: \(.ships.withImageData)/\(.ships.total) cached, Captains: \(.captains.withImageData)/\(.captains.total) cached"')
echo "   $AUDIT"
echo ""

echo "2. First ship image (API direct)..."
API_IMG=$(curl -s -o /tmp/verify-img-api.bin -w "%{http_code}|%{size_download}" "$API_URL/api/images/1")
API_CODE="${API_IMG%|*}"; API_SIZE="${API_IMG#*|}"
echo "   HTTP $API_CODE, $API_SIZE bytes"
[ "$API_CODE" != "200" ] && echo "   FAIL: API should return 200" && exit 1
[ "$API_SIZE" -lt 1000 ] && echo "   FAIL: Image too small (likely placeholder)" && exit 1
echo "   OK"
echo ""

echo "3. First ship image (Web gallery proxy)..."
WEB_IMG=$(curl -s -o /tmp/verify-img-web.bin -w "%{http_code}|%{size_download}" "$WEB_URL/gallery/image/1?v=2")
WEB_CODE="${WEB_IMG%|*}"; WEB_SIZE="${WEB_IMG#*|}"
echo "   HTTP $WEB_CODE, $WEB_SIZE bytes"
[ "$WEB_CODE" != "200" ] && echo "   FAIL: Web gallery should return 200" && exit 1
[ "$WEB_SIZE" -lt 1000 ] && echo "   FAIL: Image too small (Web cannot reach API?)" && exit 1
echo "   OK"
echo ""

echo "4. Fleet page uses gallery/image URLs..."
FLEET=$(curl -s "$WEB_URL/fleet")
if echo "$FLEET" | grep -q 'src="/gallery/image/'; then
  echo "   OK: Fleet page uses /gallery/image/:id"
else
  echo "   FAIL: Fleet page should use /gallery/image/:id (restart Web server?)"
  exit 1
fi
echo ""

echo "5. Image content-type..."
CT=$(curl -sI "$WEB_URL/gallery/image/1?v=2" | grep -i content-type | head -1)
echo "   $CT"
echo ""

echo "=== All checks passed ==="
echo "If Fleet Roster still shows placeholders:"
echo "  1. Restart the Web server (npm start) to pick up template changes"
echo "  2. Hard refresh the browser (Ctrl+Shift+R or Cmd+Shift+R)"
echo "  3. Ensure API_URL env points to the API (Web needs it to fetch images)"
