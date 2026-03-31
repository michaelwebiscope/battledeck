#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG_FILE="/tmp/navalarchive-startup-check.log"

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

FREE_PORT="$(python3 - <<'PY'
import socket
s = socket.socket()
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)"
BASE="http://127.0.0.1:${FREE_PORT}"

echo "== Build NavalArchive.Api =="
dotnet build "$ROOT/NavalArchive.Api/NavalArchive.Api.csproj" -v minimal

echo "== Start API and capture startup logs =="
ASPNETCORE_ENVIRONMENT=Development \
DynamicLists__UseDatabaseQueryMode=true \
dotnet run --project "$ROOT/NavalArchive.Api/NavalArchive.Api.csproj" --no-launch-profile --urls "$BASE" >"$LOG_FILE" 2>&1 &
API_PID=$!
cleanup() {
  if kill -0 "$API_PID" >/dev/null 2>&1; then
    kill "$API_PID" >/dev/null 2>&1 || true
    wait "$API_PID" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

echo "== Wait for API health =="
for _ in $(seq 1 40); do
  code=$(curl -sS -o /tmp/naval-startup-health.body -w '%{http_code}' "$BASE/health" || true)
  if [[ "$code" == "200" ]]; then
    echo "OK ($code)"
    break
  fi
  sleep 1
done
[[ "${code:-}" == "200" ]] || fail "API did not become healthy on $BASE"

echo "== Assert startup log lines =="
python3 - <<'PY' "$LOG_FILE" || fail "startup logs missing expected bootstrap lines"
import re
import sys

log_file = sys.argv[1]
with open(log_file, "r", encoding="utf-8", errors="ignore") as f:
    text = f.read()

if "Connection targets: provider=" not in text:
    raise SystemExit("missing sanitized connection targets log line")
if "Index bootstrap completed. provider=" not in text:
    raise SystemExit("missing index bootstrap log line")

# Ensure we do not leak obvious credential fields in Redis line.
bad_tokens = ("password=", "user id=", "username=", "pwd=")
conn_lines = [ln.lower() for ln in text.splitlines() if "Connection targets: provider=" in ln]
for ln in conn_lines:
    if any(tok in ln for tok in bad_tokens):
        raise SystemExit("sensitive token leaked in connection targets log line")

print("OK, startup logs include rollout lines and redact secrets")
PY

echo "Startup log checks passed."
