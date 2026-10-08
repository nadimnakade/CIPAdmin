#!/usr/bin/env bash
# Smoke-tests the EXACT publish/backend folder that will be deployed.
#
# It LAUNCHES the published backend itself (Production, throwaway port) and checks:
#   1. the SPA is served from wwwroot (and is the current build),
#   2. the SPA bundle carries the new login design and no stale/SSO strings,
#   3. a real login through the running API succeeds and returns a token,
#   4. real API routes answer from the controller, not the SPA fallback.
#
# The script owns the lifecycle and the verdict. It launches the server, waits for
# readiness, and EXITS NON-ZERO if any check fails. It must never exit 0 with the
# server down: curl reports 000 when nothing is listening, and a script that only
# prints those codes (and `|| echo`) happily exits 0 on a completely dead server.
# Every check below therefore records a FAIL into RC.
set -uo pipefail

cd "$(dirname "$0")/../.." || exit 1
ROOT="$(pwd)"
PKG="$ROOT/publish/backend"
PORT="${1:-5099}"
LOG="$ROOT/publish/_smoke.log"
# Use localhost, not 127.0.0.1: the backend is launched with
# ASPNETCORE_URLS=http://localhost:$PORT, and that hostname does not always
# resolve to the loopback literal, so curling 127.0.0.1 intermittently fails.
BASE="http://localhost:$PORT"

# Server=localhost fails for SqlClient (named pipes -> Win32Exception 53); use tcp:.
CONN="${CIPADMIN_CONN:-Server=tcp:localhost,1433;Database=CIP;User ID=sa;Password=123456;TrustServerCertificate=True;}"

RC=0
fail() { echo "    FAIL: $*"; RC=1; }

[ -f "$PKG/CIPAdmin.Backend.dll" ] || { echo "FATAL: $PKG/CIPAdmin.Backend.dll not found - build first"; exit 1; }

echo "=== [0] launch published backend on :$PORT (Production) ==="
taskkill //F //IM CIPAdmin.Backend.exe >/dev/null 2>&1 || true
sleep 1
: > "$LOG"
ASPNETCORE_ENVIRONMENT=Production \
ASPNETCORE_URLS="http://localhost:$PORT" \
ConnectionStrings__Default="$CONN" \
  "$PKG/CIPAdmin.Backend.exe" > "$LOG" 2>&1 &
SRV=$!
trap 'kill $SRV >/dev/null 2>&1; taskkill //F //IM CIPAdmin.Backend.exe >/dev/null 2>&1' EXIT

# Readiness poll: a cold ASP.NET start needs ~5-10s from the published folder.
READY=0
for i in $(seq 1 40); do
  if [ "$(curl -s -o /dev/null -m 2 -w '%{http_code}' "$BASE/")" != "000" ]; then
    echo "  ready after ~$((i * 2))s (pid $SRV)"
    READY=1
    break
  fi
  kill -0 $SRV 2>/dev/null || break
  sleep 2
done
if [ "$READY" != "1" ]; then
  echo "FATAL: backend never became ready. Last 30 log lines:"
  tail -30 "$LOG"
  exit 1
fi

echo "=== [1] SPA served from wwwroot ==="
INDEX=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/")
echo "  GET /            -> $INDEX"
[ "$INDEX" = "200" ] || fail "GET / returned $INDEX, expected 200"
ASSET=$(grep -o '/assets/index-[A-Za-z0-9_-]*\.js' "$PKG/wwwroot/index.html" | head -1 | sed 's#^/##')
echo "  index.html references: $ASSET"
A_CODE=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/$ASSET")
A_SIZE=$(curl -s "$BASE/$ASSET" | wc -c)
echo "  GET /$ASSET -> $A_CODE, $A_SIZE bytes"
[ "$A_CODE" = "200" ] || fail "bundle $ASSET returned $A_CODE"
[ "$A_SIZE" -gt 1000 ] || fail "bundle $ASSET served only $A_SIZE bytes"

echo "=== [2] bundle content checks ==="
BUNDLE="$PKG/wwwroot/$ASSET"
for needle in "Welcome Back" "Manage Smarter" "Business Management System"; do
  N=$(grep -c "$needle" "$BUNDLE")
  printf '  new login design %-26s : %s\n' "\"$needle\"" "$N"
  [ "$N" -ge 1 ] || fail "bundle is missing the new login string \"$needle\""
done
for needle in "Microsoft" "Continue with" "Sign in with" "DlW04V5Y"; do
  N=$(grep -c "$needle" "$BUNDLE")
  printf '  must be absent   %-26s : %s\n' "\"$needle\"" "$N"
  [ "$N" -eq 0 ] || fail "bundle still contains stale string \"$needle\""
done

echo "=== [3] deep link falls back to the SPA ==="
D_CODE=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/punch-in")
echo "  GET /punch-in     -> $D_CODE (expect 200, served index.html)"
[ "$D_CODE" = "200" ] || fail "deep link /punch-in returned $D_CODE, expected 200"

echo "=== [4] real login through the running API ==="
BODY=$(curl -s -X POST "$BASE/CIPAdmin_API/api/auth/login" \
       -H 'Content-Type: application/json' \
       -d '{"userName":"Admin","password":"aria_1up@143"}')
echo "  $BODY" | head -c 400; echo
echo "$BODY" | grep -q '"token"' || fail "login did not return a token"

echo "=== [5] prefixed routes beat the SPA fallback ==="
# Both of these are REAL controller routes, so unauthenticated they must answer 401.
# Do not substitute a made-up route here (e.g. "api/menu"): a nonexistent route
# falls through to MapFallbackToFile and answers 200 index.html on BOTH the
# prefixed and unprefixed form, so the agreement check passes for the wrong
# reason and guards nothing. Verify the body too, not just the status code.
for route in "api/auth/session" "api/menus"; do
  P=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/CIPAdmin_API/$route")
  U=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/$route")
  printf '  /CIPAdmin_API/%-20s prefixed=%s  unprefixed=%s\n' "$route" "$P" "$U"
  [ "$P" = "$U" ] || fail "$route: prefixed and unprefixed disagree"
  # A 200 here would mean the SPA fallback swallowed an API route.
  [ "$P" = "200" ] && fail "$route: expected 401 (real route), got the SPA fallback"
  [ "$P" = "401" ] || fail "$route: expected 401, got $P"
done
SW=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/CIPAdmin_API/swagger/v1/swagger.json")
echo "  swagger json (404 = disabled, correct) -> $SW"
[ "$SW" = "404" ] || fail "swagger json returned $SW, expected 404 (disabled)"

echo "=== [6] the two endpoints that 500'd in production ==="
# These are the calls behind the bug report. Against a COMPLETE CIP database they
# return 200. Against an incomplete restore they return 500 with a JSON body naming
# the missing legacy object - that is the point: the failure must be diagnosable,
# not an empty body. Either outcome is "working"; an EMPTY 500 body is not.
TOKEN=$(echo "$BODY" | grep -o '"token":"[^"]*"' | cut -d'"' -f4)
for ep in "api/applications?page=1&pageSize=25" "api/applications/existing-loans"; do
  RESP=$(curl -s -w '\n%{http_code}' -H "Authorization: Bearer $TOKEN" "$BASE/CIPAdmin_API/$ep")
  CODE=$(echo "$RESP" | tail -1)
  JSON=$(echo "$RESP" | sed '$d')
  printf '  %-40s -> %s\n' "$ep" "$CODE"
  if [ "$CODE" = "200" ]; then
    echo "    200 with $(echo "$JSON" | wc -c) bytes of data"
  elif [ "$CODE" = "500" ]; then
    # Must name the missing object, not come back empty.
    echo "$JSON" | grep -q '"error"' \
      || fail "$ep returned a 500 with no error field: '$JSON'"
    echo "$JSON" | head -c 300; echo
  else
    fail "$ep returned $CODE, expected 200 or a diagnosable 500"
  fi
done

echo "=== [7] cleanup ==="
taskkill //F //IM CIPAdmin.Backend.exe >/dev/null 2>&1 || true
echo "  stopped"

if [ "$RC" = "0" ]; then
  echo
  echo "SMOKE OK"
else
  echo
  echo "SMOKE FAILED (see FAIL lines above); server log: $LOG"
fi
exit $RC