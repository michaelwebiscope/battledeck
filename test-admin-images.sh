#!/bin/bash
# Test admin images page locally
# Prerequisites: API on :5000, Web on :3000 (or set API_URL/PORT)

API="${API_URL:-http://localhost:5000}"
WEB="${WEB_URL:-http://localhost:3000}"

echo "=== Testing Admin Images ==="
echo "API: $API | Web: $WEB"
echo ""

echo "1. API audit..."
curl -s -o /dev/null -w "   %{http_code}" "$API/api/images/audit" && echo " OK"

echo "2. Web admin page..."
code=$(curl -s -o /dev/null -w "%{http_code}" "$WEB/admin/images")
if [ "$code" = "200" ]; then
  echo "   $code OK"
else
  echo "   $code (expected 200 - restart Web server if 500)"
fi

echo "3. Image search API..."
curl -s -X POST "$API/api/images/search" \
  -H "Content-Type: application/json" \
  -d '{"query":"Bismarck","maxCount":2}' | head -c 80
echo "..."

echo ""
echo "4. Delete image (ship 1 - dry run)..."
curl -s -o /dev/null -w "   %{http_code}" -X DELETE "$API/api/images/ship/1" && echo ""

echo ""
echo "Open in browser: $WEB/admin/images"
echo "Then: click Edit on a ship → Delete or Search for a new image"
