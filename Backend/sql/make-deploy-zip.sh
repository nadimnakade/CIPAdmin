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
# Compress-Archive is Windows PowerShell and cannot read MSYS paths like /d/...,
# so hand it native Windows paths via cygpath.
cd "$ROOT/publish" || exit 1
WIN_OUT="$(cygpath -m "$OUT")"
powershell -NoProfile -Command "Compress-Archive -Path 'frontend','backend' -DestinationPath '$WIN_OUT' -Force" >/dev/null

cd "$ROOT" || exit 1
ls -l "$OUT" | awk '{printf "zip size: %.1f MB  %s\n", $5/1048576, $9}'
echo "contents: frontend/ (SPA for /CIPadmin/)  backend/ (API for /CIPAdmin_API/)"
