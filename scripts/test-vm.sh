#!/bin/bash
# Test deployed Naval Archive on VM
# Run from a machine with network access to the VM (NSG allows allowed_source_ip)
# Usage: ./test-vm.sh [base_url]

BASE="${1:-http://20.234.15.204}"
echo "=== Naval Archive VM Test ==="
echo "Target: $BASE"
echo ""

# Basic smoke test
paths=("/" "/fleet" "/captains" "/gallery" "/compare" "/ships/1" "/captains/1")
ok=0
fail=0
for path in "${paths[@]}"; do
  code=$(curl -sLk -o /dev/null -w "%{http_code}" --connect-timeout 15 "$BASE$path")
  if [ "$code" = "200" ] || [ "$code" = "302" ]; then
    echo "  OK $code $path"
    ((ok++))
  else
    echo "  FAIL $code $path"
    ((fail++))
  fi
done

echo ""
echo "Results: $ok passed, $fail failed"

# Content checks
echo ""
echo "=== Content checks ==="
fleet=$(curl -sLk --connect-timeout 15 "$BASE/fleet" | head -c 2000)
if echo "$fleet" | grep -q "Fleet Roster"; then
  echo "  OK Fleet page has title"
else
  echo "  FAIL Fleet page missing expected content"
fi

if echo "$fleet" | grep -q "entity-image\|entity-card\|Bismarck\|Tirpitz"; then
  echo "  OK Fleet shows ship data"
else
  echo "  WARN Fleet content may be empty or different"
fi

echo ""
echo "Done. If connection failed, ensure your IP is in the VM NSG (terraform output allowed_source_ip)."
