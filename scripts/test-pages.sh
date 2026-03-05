#!/bin/bash
# Quick smoke test for Naval Archive Web pages
# Prerequisites: API on :5000, Web on :3000 (or set WEB_URL)

WEB="${WEB_URL:-http://localhost:3000}"

echo "=== Naval Archive Web Smoke Test ==="
echo "Target: $WEB"
echo ""

paths=("/" "/fleet" "/captains" "/gallery" "/compare" "/ships/1" "/captains/1")
all_ok=true

for path in "${paths[@]}"; do
  code=$(curl -s -o /dev/null -w "%{http_code}" "$WEB$path")
  if [ "$code" = "200" ] || [ "$code" = "302" ]; then
    echo "  $code $path"
  else
    echo "  $code $path (FAIL)"
    all_ok=false
  fi
done

echo ""
if $all_ok; then
  echo "All pages OK."
else
  echo "Some pages failed. Restart the Web server to pick up template changes."
  exit 1
fi
