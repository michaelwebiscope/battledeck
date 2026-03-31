#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG_FILE="/tmp/navalarchive-dbmode-api.log"

if [[ -n "${API_TEST_URL:-}" ]]; then
  BASE="$API_TEST_URL"
else
  FREE_PORT="$(python3 - <<'PY'
import socket
s = socket.socket()
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)"
  BASE="http://127.0.0.1:${FREE_PORT}"
fi

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

echo "== Build NavalArchive.Api =="
dotnet build "$ROOT/NavalArchive.Api/NavalArchive.Api.csproj" -v minimal

echo "== Start API with DB mode enabled on $BASE =="
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
  code=$(curl -sS -o /tmp/naval-dbmode-health.body -w '%{http_code}' "$BASE/health" || true)
  if [[ "$code" == "200" ]]; then
    echo "OK ($code)"
    break
  fi
  sleep 1
done
[[ "${code:-}" == "200" ]] || fail "API did not become healthy on $BASE"

code=$(curl -sS -o /tmp/naval-dbmode-routecheck.body -w '%{http_code}' "$BASE/api/lists/ships?page=1&pageSize=1" || true)
[[ "$code" == "200" ]] || fail "/api/lists/ships expected HTTP 200 from test API at $BASE, got $code"

echo "== Validate DB-mode list behavior =="
python3 - <<'PY' "$BASE" || fail "dynamic list DB-mode assertions failed"
import json
import os
import sys
import urllib.parse
import urllib.request

base = sys.argv[1]
max_fallback_rate = float(os.environ.get("DLF_MAX_FALLBACK_RATE", "0.50"))

def get_json(path: str):
    with urllib.request.urlopen(base + path, timeout=15) as r:
        code = r.status
        body = json.loads(r.read().decode("utf-8"))
        return code, body

def assert_true(cond, msg):
    if not cond:
        raise AssertionError(msg)

tests = [
    ("ships_q", "/api/lists/ships?q=enterprise&page=1&pageSize=5", True, None),
    ("ships_id", "/api/lists/ships?df=" + urllib.parse.quote(json.dumps({"id": "1"})) + "&page=1&pageSize=5", True, None),
    ("classes_country", "/api/lists/classes?df=" + urllib.parse.quote(json.dumps({"country": "Japan"})) + "&page=1&pageSize=5", True, None),
    ("captains_serviceyears", "/api/lists/captains?df=" + urllib.parse.quote(json.dumps({"serviceYears": {"min": 5, "max": 60}})) + "&page=1&pageSize=5", True, None),
    ("logs_logdate_range", "/api/lists/logs?df=" + urllib.parse.quote(json.dumps({"logDate": {"from": "1942-01-01", "to": "1946-01-01"}})) + "&page=1&pageSize=5", "DB_OR_FALLBACK", "DB_OR_FALLBACK"),
]

for name, path, expect_db_search, expect_fallback in tests:
    code, body = get_json(path)
    assert_true(code == 200, f"{name}: expected 200 got {code}")
    hints = body.get("runtimeHints", {})
    used_db_search = bool(hints.get("usedDatabaseSearch"))
    fallback = hints.get("dbFallbackReason")
    if expect_db_search == "DB_OR_FALLBACK" and expect_fallback == "DB_OR_FALLBACK":
      ok_db = used_db_search and fallback in (None, "")
      ok_fallback = (not used_db_search) and isinstance(fallback, str) and len(fallback) > 0
      assert_true(ok_db or ok_fallback, f"{name}: expected DB path or graceful fallback, got usedDatabaseSearch={used_db_search}, fallback={fallback}")
      continue
    assert_true(used_db_search == expect_db_search, f"{name}: usedDatabaseSearch={used_db_search}, expected={expect_db_search}")
    if expect_fallback == "ANY_FALLBACK":
      assert_true(isinstance(fallback, str) and len(fallback) > 0, f"{name}: expected a fallback reason, got {fallback}")
    elif expect_fallback is None:
      assert_true(fallback in (None, ""), f"{name}: unexpected fallback reason {fallback}")
    else:
      assert_true(fallback == expect_fallback, f"{name}: expected fallback={expect_fallback}, got={fallback}")

code, diag = get_json("/api/dynamic-lists/diagnostics")
assert_true(code == 200, f"diagnostics: expected 200 got {code}")
counters = diag.get("counters", {})
assert_true(counters.get("requests", 0) >= len(tests), "diagnostics: requests counter did not increment")
rates = diag.get("rates", {})
fallback_rate = rates.get("fallbackRate")
assert_true(isinstance(fallback_rate, (int, float)), f"diagnostics: fallbackRate missing/invalid ({fallback_rate})")
assert_true(fallback_rate <= max_fallback_rate, f"fallbackRate {fallback_rate} exceeds threshold {max_fallback_rate}")
print("OK, DB-mode and fallback assertions passed")
PY

echo "All DB-mode checks passed."
