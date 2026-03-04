#!/bin/bash
# Test image populate (Wikipedia + Google fallback).
# Captains need at least one: PEXELS_API_KEY, PIXABAY_API_KEY, UNSPLASH_ACCESS_KEY, or GOOGLE_API_KEY+GOOGLE_CSE_ID
# Usage: PEXELS_API_KEY=xxx ./scripts/test-image-populate.sh
#        ./scripts/test-image-populate.sh captains  # test captains only

set -e
cd "$(dirname "$0")/.."
API_URL="${API_URL:-http://localhost:5000}"
MODE="${1:-all}"

echo "=== Image Populate Test ==="
echo "API: $API_URL"
echo "Image search: Pexels=${PEXELS_API_KEY:+ok} Pixabay=${PIXABAY_API_KEY:+ok} Unsplash=${UNSPLASH_ACCESS_KEY:+ok} Google=${GOOGLE_API_KEY:+ok}"
echo ""

echo "1. Audit..."
curl -s "$API_URL/api/images/audit" | jq -r '"Ships: \(.ships.total) total, \(.ships.withImageData) cached\nCaptains: \(.captains.total) total, \(.captains.withImageData) cached"'
echo ""

if [ "$MODE" = "captains" ] || [ "$MODE" = "all" ]; then
  echo "2. Populate captains 1-3 (need Google when no ImageUrl)..."
  for id in 1 2 3; do
    r=$(curl -s -X POST "$API_URL/api/images/populate/captain/$id" -H "Content-Type: application/json")
    echo "   Captain $id: $r"
  done
  echo ""
fi

if [ "$MODE" = "ships" ] || [ "$MODE" = "all" ]; then
  echo "3. Populate ship 1 (Bismarck)..."
  curl -s -X POST "$API_URL/api/images/populate/ship/1" -H "Content-Type: application/json" | jq .
  echo ""
fi

echo "4. Audit again..."
curl -s "$API_URL/api/images/audit" | jq -r '"Ships: \(.ships.total) total, \(.ships.withImageData) cached\nCaptains: \(.captains.total) total, \(.captains.withImageData) cached"'
echo "Done."
