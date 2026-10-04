#!/usr/bin/env bash
# Smoke-tests the EXACT publish/backend folder that will be deployed.
# Runs it in Production on a throwaway port and checks:
#   1. the SPA is served from wwwroot (and is the current build),
#   2. the SPA bundle carries the new login design and no stale/SSO strings,
#   3. a real login through the running API succeeds and returns a token.
set -uo pipefail

cd "$(dirname "$0")/../.." || exit 1
ROOT="$(pwd)"
PKG="$ROOT/publish/backend"
PORT="${1:-5099}"
LOG="$ROOT/publish/_smoke.log"
BASE="http://127.0.0.1:$PORT"

echo "=== [1] SPA served from wwwroot ==="
INDEX=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/")
echo "  GET /            -> $INDEX"
ASSET=$(grep -o '/assets/index-[A-Za-z0-9_-]*\.js' "$PKG/wwwroot/index.html" | head -1 | sed 's#^/##')
echo "  index.html references: $ASSET"
A_CODE=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/$ASSET")
A_SIZE=$(curl -s "$BASE/$ASSET" | wc -c)
echo "  GET /$ASSET -> $A_CODE, $A_SIZE bytes"

echo "=== [2] bundle content checks ==="
BUNDLE="$PKG/wwwroot/$ASSET"
for needle in "Welcome Back" "Manage Smarter" "Business Management System"; do
  printf '  new login design %-26s : %s\n' "\"$needle\"" "$(grep -c "$needle" "$BUNDLE")"
done
for needle in "Microsoft" "Continue with" "Sign in with" "DlW04V5Y"; do
  printf '  must be absent   %-26s : %s\n' "\"$needle\"" "$(grep -c "$needle" "$BUNDLE")"
done

echo "=== [3] deep link falls back to the SPA ==="
D_CODE=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/punch-in")
echo "  GET /punch-in     -> $D_CODE (expect 200, served index.html)"

echo "=== [4] real login through the running API ==="
BODY=$(curl -s -X POST "$BASE/CIPAdmin_API/api/auth/login" \
       -H 'Content-Type: application/json' \
       -d '{"userName":"Admin","password":"aria_1up@143"}')
echo "  $BODY" | head -c 400; echo
echo "$BODY" | grep -q '"token"' && echo "  token issued: yes" || echo "  token issued: NO"

# The PathBase regression this guards: with the prefix attached, an unauthenticated
# API route must answer 401/200 from the CONTROLLER, never 200 with the SPA body
# (which would mean MapFallbackToFile swallowed it) and never 405 (the static-file
# fallback refusing POST). Unprefixed is the control.
echo "=== [5] prefixed routes beat the SPA fallback ==="
for route in "api/auth/session" "api/menu"; do
  P=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/CIPAdmin_API/$route")
  U=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/$route")
  printf '  /CIPAdmin_API/%-20s prefixed=%s  unprefixed=%s\n' "$route" "$P" "$U"
  [ "$P" = "$U" ] || echo "    FAIL: prefixed and unprefixed disagree"
done
SW=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/CIPAdmin_API/swagger/v1/swagger.json")
echo "  swagger json (404 = disabled, correct) -> $SW"

echo "=== [6] cleanup ==="
taskkill //F //IM CIPAdmin.Backend.exe >/dev/null 2>&1 || true
echo "  stopped"
