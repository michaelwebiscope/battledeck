#!/usr/bin/env bash
set -euo pipefail

CONTAINER_NAME="${POSTGRES_CONTAINER_NAME:-navalarchive-postgres}"
POSTGRES_USER="${POSTGRES_USER:-navalarchive}"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-navalarchive_dev}"
POSTGRES_PORT="${POSTGRES_PORT:-5432}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INIT_SQL="$SCRIPT_DIR/postgres/init-navalarchive.sql"

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is required"
  exit 1
fi

if [ ! -f "$INIT_SQL" ]; then
  echo "init SQL not found: $INIT_SQL"
  exit 1
fi

if docker ps -a --format '{{.Names}}' | rg -x "$CONTAINER_NAME" >/dev/null 2>&1; then
  docker rm -f "$CONTAINER_NAME" >/dev/null
fi

docker run -d \
  --name "$CONTAINER_NAME" \
  -e POSTGRES_USER="$POSTGRES_USER" \
  -e POSTGRES_PASSWORD="$POSTGRES_PASSWORD" \
  -e POSTGRES_DB=postgres \
  -p "$POSTGRES_PORT:5432" \
  -v "$INIT_SQL:/docker-entrypoint-initdb.d/init-navalarchive.sql:ro" \
  postgres:16 >/dev/null

echo "PostgreSQL started in container: $CONTAINER_NAME"
echo ""
echo "Use these connection strings in NavalArchive.Api/appsettings.Development.json:"
echo "  \"DatabaseProvider\": \"Postgres\""
echo "  \"ConnectionStrings\": {"
echo "    \"NavalArchiveDb\": \"Host=localhost;Port=$POSTGRES_PORT;Database=navalarchive;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD\","
echo "    \"LogsDb\": \"Host=localhost;Port=$POSTGRES_PORT;Database=navalarchive_logs;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD\""
echo "  }"
