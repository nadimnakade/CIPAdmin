#!/usr/bin/env bash
# End-to-end test for the admin Error Log feature.
#
# It answers one question the build cannot: does a REAL database failure actually
# land in TBL_CIP_API_ERROR_LOG with the fields the screen shows, and does the
# admin-only API page and filter it correctly?
#
# This exists because a unit test would not have caught the bug it was written for:
# the list endpoint's row mapper read QUERY_STRING while the query never selected
# it, so every request failed with "Column 'QUERY_STRING' does not belong to table
# Table". The code compiled, the endpoint existed, and only calling it revealed it.
#
# It works by BREAKING the database on purpose (renaming a proc the endpoint calls),
# calling the endpoint, and asserting the failure is captured - then restoring it.
# Every rename is reversed even if an assertion fails.
#
# Usage: bash Backend/sql/test-error-log.sh [port]
set -uo pipefail

cd "$(dirname "$0")/../.." || exit 1
ROOT="$(pwd)"
PORT="${1:-5097}"
BASE="http://localhost:$PORT"
SQLCMD="/c/Program Files/Microsoft SQL Server/Client SDK/ODBC/170/Tools/Binn/sqlcmd"
CONN="${CIPADMIN_CONN:-Server=tcp:localhost,1433;Database=CIP;User ID=sa;Password=123456;TrustServerCertificate=True;}"
DB=$(echo "$CONN" | sed -n 's/.*Database=\([^;]*\).*/\1/p')

# Python here is NATIVE WINDOWS and cannot read MSYS paths like /d/Git_Repos/...,
# so every path handed to it is converted with cygpath. Getting this wrong makes
# every assertion fail on FileNotFoundError while the feature works perfectly.
WJSON="$(cygpath -m "$ROOT/publish/_el.json")"

RC=0
pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*"; RC=1; }

# SET NOCOUNT ON is not cosmetic: without it sqlcmd appends "(1 rows affected)"
# to the output, which a command substitution then compares as part of the value.
sqlq() { "$SQLCMD" -S tcp:localhost,1433 -d "$DB" -U sa -P 123456 -C -b -h -1 -W -l 30 -Q "SET NOCOUNT ON; $1" | tr -d '\r'; }

# Read a value out of the last saved response.
jqv() { python -c "
import json
try:
    d = json.load(open(r'''$WJSON''', encoding='utf-8'))
    print($1)
except Exception as e:
    print('ERR:%s' % e)
"; }

scalar() { sqlq "$1" | head -1 | tr -d ' '; }

cleanup() {
  # Always put the database back, even on failure. A half-renamed database is far
  # worse than a failing test.
  taskkill //F //IM CIPAdmin.Backend.exe >/dev/null 2>&1 || true
  sqlq "IF OBJECT_ID('dbo.USP_CIP_EXISTINGLOAN_DETAILS_TESTBAK','P') IS NOT NULL EXEC sp_rename 'dbo.USP_CIP_EXISTINGLOAN_DETAILS_TESTBAK','USP_CIP_EXISTINGLOAN_DETAILS'" >/dev/null
  sqlq "DELETE FROM dbo.TBL_CIP_API_ERROR_LOG" >/dev/null
}
trap cleanup EXIT

echo "=== [0] preconditions ==="
[ -f "$ROOT/publish/backend/CIPAdmin.Backend.exe" ] || { echo "FATAL: publish/backend missing - run Backend/sql/build-deploy.sh"; exit 1; }
[ "$(scalar "SELECT CASE WHEN OBJECT_ID('dbo.TBL_CIP_API_ERROR_LOG','U') IS NULL THEN 'NO' ELSE 'YES' END")" = "YES" ] \
  || { echo "FATAL: TBL_CIP_API_ERROR_LOG missing - run Backend/sql/ERROR-LOG.sql first"; exit 1; }
echo "  TBL_CIP_API_ERROR_LOG present in $DB"

cleanup   # clear rows and make sure no rename is left over from a previous run

echo "=== [1] launch published backend on :$PORT ==="
ASPNETCORE_ENVIRONMENT=Production \
ASPNETCORE_URLS="http://localhost:$PORT" \
ConnectionStrings__Default="$CONN" \
  "$ROOT/publish/backend/CIPAdmin.Backend.exe" > "$ROOT/publish/_errorlog_test.log" 2>&1 &
for i in $(seq 1 40); do
  [ "$(curl -s -o /dev/null -m 2 -w '%{http_code}' "$BASE/")" != "000" ] && { echo "  ready after ~$((i*2))s"; break; }
  sleep 2
done
[ "$(curl -s -o /dev/null -m 2 -w '%{http_code}' "$BASE/")" != "000" ] || { echo "FATAL: never became ready"; tail -20 "$ROOT/publish/_errorlog_test.log"; exit 1; }

echo "=== [2] the log endpoint is admin-only ==="
code=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/CIPAdmin_API/api/error-logs")
[ "$code" = "401" ] && pass "no token -> 401" || fail "no token -> $code (expected 401)"

TOKEN=$(curl -s -X POST "$BASE/CIPAdmin_API/api/auth/login" -H 'Content-Type: application/json' \
        -d '{"userName":"Admin","password":"aria_1up@143"}' | grep -o '"token":"[^"]*"' | cut -d'"' -f4)
[ -n "$TOKEN" ] || { echo "FATAL: could not log in as Admin"; exit 1; }
H="Authorization: Bearer $TOKEN"
echo "  logged in as Admin"

echo "=== [3] baseline: log is empty and readable ==="
curl -s -H "$H" "$BASE/CIPAdmin_API/api/error-logs?page=1&pageSize=5" -o "$ROOT/publish/_el.json"
[ "$(jqv "d['total']")" = "0" ] && pass "total=0 before any failure" || fail "expected total=0, got $(jqv "d['total']")"
[ "$(jqv "d.get('logTablePresent')")" = "True" ] && pass "logTablePresent=true" || fail "logTablePresent not true"

echo "=== [4] a CAUGHT database error is recorded (controller catches, returns 500) ==="
sqlq "IF OBJECT_ID('dbo.USP_CIP_EXISTINGLOAN_DETAILS','P') IS NOT NULL EXEC sp_rename 'dbo.USP_CIP_EXISTINGLOAN_DETAILS','USP_CIP_EXISTINGLOAN_DETAILS_TESTBAK'" >/dev/null
code=$(curl -s -o /dev/null -w '%{http_code}' -H "$H" "$BASE/CIPAdmin_API/api/applications/existing-loans")
[ "$code" = "500" ] && pass "endpoint returned 500 with the proc missing" || fail "expected 500, got $code"
sleep 2

ROW=$(scalar "SELECT CONCAT(ISNULL(SOURCE,'~'),'|',ISNULL(EXCEPTION_TYPE,'~'),'|',ISNULL(CAST(SQL_ERROR_NUMBER AS varchar(10)),'~'),'|',ISNULL(DB_OBJECT,'~'),'|',ISNULL(CAST(EMP_CODE AS varchar(10)),'~'),'|',ISNULL(CAST(STATUS_CODE AS varchar(10)),'~'),'|',ISNULL(CAST(LEN(STACK_TRACE) AS varchar(10)),'0')) FROM dbo.TBL_CIP_API_ERROR_LOG ORDER BY ERROR_ID DESC")
echo "  row: $ROW"
# Strip the trailing stack-length field (variable), keep the fixed leading fields.
FIELDS=$(python -c "print('|'.join(r'''$ROW'''.split('|')[:6]))")
[ "$FIELDS" = "Database|Microsoft.Data.SqlClient.SqlException|2812|dbo.USP_CIP_EXISTINGLOAN_DETAILS|10|500" ] \
  && pass "classified Database/SqlException/2812, names the object, records user + 500" \
  || fail "row fields unexpected: $FIELDS"

STACKLEN=$(python -c "f=r'''$ROW'''.split('|'); print(f[6] if len(f) > 6 else '0')")
[ "${STACKLEN:-0}" -gt 200 ] && pass "stack trace captured ($STACKLEN chars)" || fail "stack trace too short: $STACKLEN"

echo "=== [5] recorded exactly once (no double logging) ==="
N=$(scalar "SELECT COUNT(*) FROM dbo.TBL_CIP_API_ERROR_LOG")
[ "$N" = "1" ] && pass "exactly 1 row for 1 request" || fail "expected 1 row, got '$N' - middleware and exception handler both logged"

echo "=== [6] the list endpoint pages and filters ==="
curl -s -H "$H" "$BASE/CIPAdmin_API/api/error-logs?page=1&pageSize=5" -o "$ROOT/publish/_el.json"
[ "$(jqv "d['total']")" = "1" ] && pass "total=1" || fail "expected total=1, got $(jqv "d['total']")"
[ "$(jqv "d['page']")" = "1" ] && pass "page=1 echoed" || fail "page echo wrong"
# The bug this file was written for: a mapper reading a column the query omits.
[ "$(jqv "d['rows'][0]['requestPath']")" = "/api/applications/existing-loans" ] \
  && pass "row maps requestPath (mapper and SELECT list agree)" \
  || fail "row did not map requestPath: $(jqv "d['rows'][0]")"
[ "$(jqv "d['rows'][0]['sqlErrorNumber']")" = "2812" ] && pass "row maps sqlErrorNumber" || fail "sqlErrorNumber missing"

echo "  -- pageSize is clamped --"
curl -s -H "$H" "$BASE/CIPAdmin_API/api/error-logs?pageSize=9999" -o "$ROOT/publish/_el.json"
[ "$(jqv "d['pageSize']")" = "200" ] && pass "pageSize 9999 clamped to 200" || fail "pageSize not clamped: $(jqv "d['pageSize']")"

echo "  -- page is clamped --"
curl -s -H "$H" "$BASE/CIPAdmin_API/api/error-logs?page=0" -o "$ROOT/publish/_el.json"
[ "$(jqv "d['page']")" = "1" ] && pass "page 0 clamped to 1" || fail "page 0 not clamped: $(jqv "d['page']")"

echo "  -- source filter --"
curl -s -H "$H" "$BASE/CIPAdmin_API/api/error-logs?source=Database" -o "$ROOT/publish/_el.json"
[ "$(jqv "d['total']")" = "1" ] && pass "source=Database matches" || fail "source=Database total=$(jqv "d['total']")"
curl -s -H "$H" "$BASE/CIPAdmin_API/api/error-logs?source=Application" -o "$ROOT/publish/_el.json"
[ "$(jqv "d['total']")" = "0" ] && pass "source=Application excludes it" || fail "source=Application total=$(jqv "d['total']")"

echo "  -- free-text search reaches the SQL error number --"
curl -s -H "$H" "$BASE/CIPAdmin_API/api/error-logs?q=2812" -o "$ROOT/publish/_el.json"
[ "$(jqv "d['total']")" = "1" ] && pass "q=2812 finds the row by SQL error number" || fail "q=2812 total=$(jqv "d['total']")"

echo "=== [7] detail returns the stack trace ==="
ID=$(jqv "d['rows'][0]['id']")
[ -n "$ID" ] && [ "${ID#ERR:}" = "$ID" ] || { echo "FATAL: could not read a row id ('$ID')"; exit 1; }
curl -s -H "$H" "$BASE/CIPAdmin_API/api/error-logs/$ID" -o "$ROOT/publish/_el.json"
LEN=$(jqv "len(d.get('stackTrace') or '')")
[ "${LEN:-0}" -gt 200 ] && pass "detail returns a $LEN-char stack trace" || fail "stack trace too short: $LEN"
code=$(curl -s -o /dev/null -w '%{http_code}' -H "$H" "$BASE/CIPAdmin_API/api/error-logs/999999999")
[ "$code" = "404" ] && pass "unknown id -> 404" || fail "unknown id -> $code"

echo "=== [8] resolve requires a note, then filters out ==="
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "$H" -H 'Content-Type: application/json' \
       -d '{"resolved":true}' "$BASE/CIPAdmin_API/api/error-logs/$ID/resolve")
[ "$code" = "400" ] && pass "resolving without a note -> 400" || fail "expected 400, got $code"

code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "$H" -H 'Content-Type: application/json' \
       -d '{"resolved":true,"note":"Proc restored from backup."}' "$BASE/CIPAdmin_API/api/error-logs/$ID/resolve")
[ "$code" = "200" ] && pass "resolving with a note -> 200" || fail "expected 200, got $code"

curl -s -H "$H" "$BASE/CIPAdmin_API/api/error-logs?resolved=false" -o "$ROOT/publish/_el.json"
[ "$(jqv "d['total']")" = "0" ] && pass "resolved=false now excludes it" || fail "resolved=false total=$(jqv "d['total']")"
curl -s -H "$H" "$BASE/CIPAdmin_API/api/error-logs?resolved=true" -o "$ROOT/publish/_el.json"
[ "$(jqv "d['total']")" = "1" ] && pass "resolved=true includes it" || fail "resolved=true total=$(jqv "d['total']")"

echo "=== [9] non-error responses are NOT logged ==="
before=$(scalar "SELECT COUNT(*) FROM dbo.TBL_CIP_API_ERROR_LOG")
curl -s -o /dev/null -H "$H" "$BASE/CIPAdmin_API/api/applications?page=1&pageSize=25"   # 200
curl -s -o /dev/null -H "$H" "$BASE/CIPAdmin_API/api/applications/999999999"              # 404
curl -s -o /dev/null -H "$H" "$BASE/CIPAdmin_API/api/error-logs"                        # 200
sleep 2
after=$(scalar "SELECT COUNT(*) FROM dbo.TBL_CIP_API_ERROR_LOG")
[ "$before" = "$after" ] && pass "200s and 404s add no rows (still $after)" || fail "row count changed $before -> $after; only 5xx should log"

echo "=== [10] purge respects its floor ==="
curl -s -X POST -H "$H" -H 'Content-Type: application/json' -d '{"olderThanDays":0}' \
     "$BASE/CIPAdmin_API/api/error-logs/purge" -o "$ROOT/publish/_el.json"
DAYS=$(jqv "d['olderThanDays']")
[ "$DAYS" = "1" ] && pass "olderThanDays 0 clamped to 1 (cannot wipe the table)" || fail "purge clamp wrong: $DAYS"

rm -f "$ROOT/publish/_el.json"

echo
if [ "$RC" = "0" ]; then echo "ERROR LOG TESTS PASSED"; else echo "ERROR LOG TESTS FAILED"; fi
exit $RC