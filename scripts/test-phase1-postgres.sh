#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

PG_USER="${POSTGRES_USER:-navalarchive}"
PG_PASSWORD="${POSTGRES_PASSWORD:-navalarchive_dev}"
PG_CONTAINER_NAME="${POSTGRES_CONTAINER_NAME:-navalarchive-postgres-phase1}"
PG_PORT="${POSTGRES_PORT:-$(python3 - <<'PY'
import socket
s = socket.socket()
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)}"
API_BASE="${API_TEST_URL:-http://127.0.0.1:$(python3 - <<'PY'
import socket
s = socket.socket()
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)}"

MAIN_CONN="Host=localhost;Port=${PG_PORT};Database=navalarchive;Username=${PG_USER};Password=${PG_PASSWORD}"
LOGS_CONN="Host=localhost;Port=${PG_PORT};Database=navalarchive_logs;Username=${PG_USER};Password=${PG_PASSWORD}"

API_PID=""
cleanup() {
  if [[ -n "$API_PID" ]] && kill -0 "$API_PID" >/dev/null 2>&1; then
    kill "$API_PID" >/dev/null 2>&1 || true
    wait "$API_PID" >/dev/null 2>&1 || true
  fi
  if command -v docker >/dev/null 2>&1; then
    docker rm -f "$PG_CONTAINER_NAME" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

if ! command -v docker >/dev/null 2>&1; then
  echo "FAIL: docker CLI is required for Postgres gate"
  exit 1
fi
if ! docker info >/dev/null 2>&1; then
  echo "FAIL: docker daemon is not running or not reachable"
  echo "Start Docker Desktop (or daemon), then rerun ./scripts/test-phase1-postgres.sh"
  exit 1
fi

echo "== Phase 1 Postgres Gate: Start PostgreSQL =="
POSTGRES_USER="$PG_USER" \
POSTGRES_PASSWORD="$PG_PASSWORD" \
POSTGRES_PORT="$PG_PORT" \
POSTGRES_CONTAINER_NAME="$PG_CONTAINER_NAME" \
"$ROOT/scripts/start-postgres-local.sh"

echo "== Wait for PostgreSQL readiness =="
for _ in $(seq 1 40); do
  if docker exec "$PG_CONTAINER_NAME" pg_isready -U "$PG_USER" -d postgres >/dev/null 2>&1; then
    echo "PostgreSQL is ready on port $PG_PORT"
    break
  fi
  sleep 1
done
docker exec "$PG_CONTAINER_NAME" pg_isready -U "$PG_USER" -d postgres >/dev/null 2>&1

echo "== Phase 1 Postgres Gate: API smoke =="
ASPNETCORE_ENVIRONMENT=Development \
DatabaseProvider=Postgres \
ConnectionStrings__NavalArchiveDb="$MAIN_CONN" \
ConnectionStrings__LogsDb="$LOGS_CONN" \
dotnet run --project "$ROOT/NavalArchive.Api/NavalArchive.Api.csproj" --no-launch-profile --urls "$API_BASE" >/tmp/navalarchive-phase1-postgres-api.log 2>&1 &
API_PID=$!

for _ in $(seq 1 40); do
  code=$(curl -sS -o /tmp/naval-pg-health.body -w '%{http_code}' "$API_BASE/health" || true)
  if [[ "$code" == "200" ]]; then
    break
  fi
  sleep 1
done
[[ "${code:-}" == "200" ]] || { echo "API failed to start on $API_BASE"; exit 1; }
API_TEST_URL="$API_BASE" "$ROOT/scripts/test-api.sh"

kill "$API_PID" >/dev/null 2>&1 || true
wait "$API_PID" >/dev/null 2>&1 || true
API_PID=""

echo ""
echo "== Phase 1 Postgres Gate: Dynamic lists DB mode =="
DatabaseProvider=Postgres \
ConnectionStrings__NavalArchiveDb="$MAIN_CONN" \
ConnectionStrings__LogsDb="$LOGS_CONN" \
"$ROOT/scripts/test-dynamic-lists-db-mode.sh"

echo ""
echo "== Phase 1 Postgres Gate: Startup rollout logs =="
DatabaseProvider=Postgres \
ConnectionStrings__NavalArchiveDb="$MAIN_CONN" \
ConnectionStrings__LogsDb="$LOGS_CONN" \
"$ROOT/scripts/test-startup-logs.sh"

echo ""
echo "Phase 1 Postgres gate passed."
