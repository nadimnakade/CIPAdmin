#!/usr/bin/env bash
# Builds publish/CIPAdmin-deploy.zip from the two publish folders.
# Re-run after any republish. The zip is the artifact you copy to the server.
set -euo pipefail

cd "$(dirname "$0")/../.." || exit 1   # Backend/sql/ -> CIPAdmin/
ROOT="$(pwd)"
OUT="$ROOT/publish/CIPAdmin-deploy.zip"

[ -f "$ROOT/publish/backend/CIPAdmin.Backend.dll" ] || { echo "backend publish missing - run dotnet publish first" >&2; exit 1; }
[ -f "$ROOT/publish/frontend/index.html" ]         || { echo "frontend build missing - run vite build first" >&2; exit 1; }

rm -f "$OUT"

# The deploy SQL is part of the deliverable, not just a repo convenience. The operator
# has to run DIAGNOSE-500.sql and the ordered deploy scripts ON THE PRODUCTION SERVER,
# and the repo is not reachable from there. Mirror them into publish/sql/ so the zip is
# self-sufficient. Copy rather than zip in place, because Compress-Archive takes
# relative names from publish/ and would bake in an absolute Developer-Machine path.
rm -rf "$ROOT/publish/sql"
mkdir -p "$ROOT/publish/sql"
for f in "$ROOT"/Database/*.sql "$ROOT"/Backend/sql/*.sql; do
  [ -f "$f" ] || continue
  cp "$f" "$ROOT/publish/sql/"
done
N_SQL=$(ls -1 "$ROOT/publish/sql"/*.sql 2>/dev/null | wc -l)
[ "$N_SQL" -gt 0 ] || { echo "no deploy SQL found to package" >&2; exit 1; }
echo "packaged $N_SQL deploy SQL scripts into publish/sql/"

# The runbook travels WITH the scripts. Without it the package is a folder of 19 SQL
# files with no indication of which to run, in what order, or that two of them must
# run in a specific sequence (ERROR-LOG-MENU.sql needs ERROR-LOG.sql first). An
# operator handed only the zip would have to guess.
cp "$ROOT/DEPLOY.md" "$ROOT/publish/DEPLOY.md"

# Compress-Archive is Windows PowerShell and cannot read MSYS paths like /d/...,
# so hand it native Windows paths via cygpath.
cd "$ROOT/publish" || exit 1
WIN_OUT="$(cygpath -m "$OUT")"
powershell -NoProfile -Command "Compress-Archive -Path 'frontend','backend','sql','DEPLOY.md' -DestinationPath '$WIN_OUT' -Force" >/dev/null

cd "$ROOT" || exit 1
ls -l "$OUT" | awk '{printf "zip size: %.1f MB  %s\n", $5/1048576, $9}'
echo "contents: frontend/ (SPA for /CIPadmin/)  backend/ (API for /CIPAdmin_API/)  sql/ (run on the server)"

# Gate on what actually landed in the archive. Compress-Archive happily packs up
# whatever is sitting in publish/, including server logs and scratch files left by a
# local smoke test, so the contents are verified rather than assumed.
python "$ROOT/Backend/sql/check-zip.py"
