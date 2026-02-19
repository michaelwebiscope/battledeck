#!/usr/bin/env bash
# Run all Naval Archive services locally for development.
# Services: Payment (5001), Card (5002), Cart (5003), API (5000), Web (3000)

set -e
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PIDS=()
cleanup() {
  echo ""
  echo "Stopping services..."
  for pid in "${PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  wait 2>/dev/null || true
  echo "Done."
  exit 0
}
trap cleanup SIGINT SIGTERM

echo "Starting Naval Archive (local)..."
echo "  Payment:  http://localhost:5001"
echo "  Card:     http://localhost:5002"
echo "  Cart:     http://localhost:5003"
echo "  API:      http://localhost:5000"
echo "  Web:      http://localhost:3000"
echo ""
echo "Press Ctrl+C to stop all."
echo ""

# 1. Payment (5001)
dotnet run --project NavalArchive.PaymentSimulation &
PIDS+=($!)
sleep 2

# 2. Card (5002)
dotnet run --project NavalArchive.CardService &
PIDS+=($!)
sleep 2

# 3. Cart (5003)
dotnet run --project NavalArchive.CartService &
PIDS+=($!)
sleep 2

# 4. API (5000)
dotnet run --project NavalArchive.Api &
PIDS+=($!)
sleep 2

# 5. Web (3000)
(cd "$ROOT/NavalArchive.Web" && node server.js) &
PIDS+=($!)

echo "All services started. Open http://localhost:3000"
wait
