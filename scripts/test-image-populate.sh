#!/bin/bash
# Test image populate (Wikipedia + Google fallback).
# Set GOOGLE_API_KEY and GOOGLE_CSE_ID to test Google fallback.
# Usage: ./scripts/test-image-populate.sh

set -e
cd "$(dirname "$0")/.."
API_URL="${API_URL:-http://localhost:5000}"

echo "=== Image Populate Test ==="
echo "API: $API_URL"
echo "Google configured: ${GOOGLE_API_KEY:+yes}${GOOGLE_API_KEY:-no}"
echo ""

echo "1. Audit..."
curl -s "$API_URL/api/images/audit" | jq -r '.ships | "Ships: \(.total) total, \(.withImageData) cached"; .captains | "Captains: \(.total) total, \(.withImageData) cached"'
echo ""

echo "2. Populate ship 1 (Bismarck)..."
curl -s -X POST "$API_URL/api/images/populate/ship/1" -H "Content-Type: application/json" | jq .
echo ""

echo "3. Populate captain 1 (Ernst Lindemann, needs Google if no ImageUrl)..."
curl -s -X POST "$API_URL/api/images/populate/captain/1" -H "Content-Type: application/json" | jq .
echo ""

echo "4. Audit again..."
curl -s "$API_URL/api/images/audit" | jq -r '.ships | "Ships: \(.total) total, \(.withImageData) cached"; .captains | "Captains: \(.total) total, \(.withImageData) cached"'
echo "Done."
