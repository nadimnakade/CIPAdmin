#!/usr/bin/env bash
# Negative test for DEPLOY-CHECK.sql section 3.
# Simulates a production server that never got APPLICATION-MODULES-MENU.sql by
# removing one grant (role 2012 -> New Application), confirms the probe reports
# MISSING, then restores it with the migration and confirms the probe is clean.
set -uo pipefail
cd "$(dirname "$0")/../../.." || exit 1

SQLCMD="/c/Program Files/Microsoft SQL Server/Client SDK/ODBC/170/Tools/Binn/sqlcmd"
CRED=(-S tcp:localhost,1433 -d CIP -U sa -P 123456 -C -b -h -1 -W -l 30)

echo "--- baseline: grant exists? ---"
"$SQLCMD" "${CRED[@]}" -Q "SELECT COUNT(*) AS grant_rows FROM dbo.TBL_CIP_ROLE_MENU WHERE ROLE_ID=2012 AND MENU_ID=2004;"
echo "EXIT=$?"

echo "--- simulate un-migrated server: drop that grant ---"
"$SQLCMD" "${CRED[@]}" -Q "DELETE FROM dbo.TBL_CIP_ROLE_MENU WHERE ROLE_ID=2012 AND MENU_ID=2004;"
echo "EXIT=$?"

echo "--- probe should now report MISSING ---"
"$SQLCMD" "${CRED[@]}" -i CIPAdmin/Backend/sql/DEPLOY-CHECK.sql | grep -E "GRANTS OK|MISSING"
echo "PROBE_EXIT=${PIPESTATUS[0]}"

echo "--- restore via the idempotent migration ---"
"$SQLCMD" "${CRED[@]}" -i CIPAdmin/Backend/sql/APPLICATION-MODULES-MENU.sql >/dev/null
echo "MIGRATION_EXIT=$?"

echo "--- probe should be clean again ---"
"$SQLCMD" "${CRED[@]}" -i CIPAdmin/Backend/sql/DEPLOY-CHECK.sql | grep -E "GRANTS OK|MISSING"
echo "PROBE_EXIT=${PIPESTATUS[0]}"
