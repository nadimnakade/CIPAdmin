#!/usr/bin/env bash
# Full clean deploy build: frontend -> backend -> zip.
#
# The clean steps are load-bearing, not cosmetic:
#   * rm -rf Frontend/dist           Vite empties its outDir, so this is just tidy.
#   * rm -rf Backend/wwwroot         The Web SDK ships wwwroot verbatim. Leaving the
#                                    old SPA in place bakes a STALE hashed bundle into
#                                    the publish and users get the previous release.
#   * rm -rf publish/backend         dotnet publish does NOT clean its output dir, so
#                                    stale hashed assets accumulate across releases.
#   * taskkill                      The running backend holds CIPAdmin.Backend.exe and
#                                    the build fails with MSB3027 otherwise.
set -euo pipefail

cd "$(dirname "$0")/../.." || exit 1   # Backend/sql/ -> CIPAdmin/
ROOT="$(pwd)"
SQLCMD="/c/Program Files/Microsoft SQL Server/Client SDK/ODBC/170/Tools/Binn/sqlcmd"

echo "==> [1/5] stopping any running backend (frees CIPAdmin.Backend.exe)"
taskkill //F //IM CIPAdmin.Backend.exe >/dev/null 2>&1 || true

echo "==> [2/5] frontend typecheck + production build (base /CIPadmin/, api /CIPAdmin_API)"
cd "$ROOT/Frontend" || exit 1
./node_modules/.bin/tsc -b --force
rm -rf dist
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' VITE_BASE=/CIPadmin/ VITE_API_BASE=/CIPAdmin_API \
  ./node_modules/.bin/vite build

echo "==> [3/5] mirroring the SPA into Backend/wwwroot (single-site mode)"
rm -rf "$ROOT/Backend/wwwroot"
mkdir -p "$ROOT/Backend/wwwroot"
cp -r "$ROOT/Frontend/dist/." "$ROOT/Backend/wwwroot/"
cp "$ROOT/Frontend/public/web.config" "$ROOT/Backend/wwwroot/web.config" 2>/dev/null || true
ls "$ROOT/Backend/wwwroot"

echo "==> [4/5] backend publish"
cd "$ROOT/Backend" || exit 1
rm -rf "$ROOT/publish/backend"
dotnet publish -c Release -o "$ROOT/publish/backend"

echo "==> [5/5] pinning production-only settings in the published appsettings.json"
python - "$ROOT/publish/backend/appsettings.json" <<'PY'
import json, sys
p = sys.argv[1]
cfg = json.load(open(p, encoding="utf-8"))
cfg["PathBase"] = "/CIPAdmin_API"
cfg.setdefault("Swagger", {})["Enabled"] = False
json.dump(cfg, open(p, "w", encoding="utf-8"), indent=2)
print("  PathBase =", cfg["PathBase"], "| Swagger.Enabled =", cfg["Swagger"]["Enabled"])
PY

echo "==> refresh publish/frontend from the fresh dist"
rm -rf "$ROOT/publish/frontend"
mkdir -p "$ROOT/publish/frontend"
cp -r "$ROOT/Frontend/dist/." "$ROOT/publish/frontend/"
cp "$ROOT/Frontend/public/web.config" "$ROOT/publish/frontend/web.config" 2>/dev/null || true

echo "==> sanity: no nested publish folder inside the output"
if [ -d "$ROOT/publish/backend/publish" ]; then
  echo "FAIL: publish/backend/publish exists - the csproj DefaultItemExcludes is not working" >&2
  exit 1
fi
echo "  ok - output is flat"

bash "$ROOT/Backend/sql/make-deploy-zip.sh"

echo
echo "Deploy SQL run-order:"
echo "  1. Database/schema.sql"
echo "  2. Backend/sql/USER-SINGLE-SESSION.sql"
echo "  3. Backend/sql/TBL_CIP_APP_SETTINGS.sql"
echo "  4. Backend/sql/CIBIL-PULL-LOG.sql"
echo "  5. Backend/sql/APPLICATION-MODULES-MENU.sql"
echo "  6. Backend/sql/PUNCHIN-MENU.sql"
echo "then run Backend/sql/DEPLOY-CHECK.sql and confirm every row says 'present'."
