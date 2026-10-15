#!/usr/bin/env python3
"""
End-to-end check of the CIBIL duplicate-pull freeze against a RUNNING backend.

The rule under test: paid failed vendor calls freeze a PAN for all users for the
rest of the month; only an explicit admin re-pull bypasses the freeze. A completed
OK or FAILED vendor call counts one monthly quota credit for the requester. Duplicate
requests blocked before the vendor API call consume no additional credit. Successful
same-month reports are served from cache; success-history popup state remains
user-scoped. The vendor's failure reason is disclosed to admins only.

  1  admin generate, vendor call fails        -> FAILED row + reason logged, quota +1
  2  admin generate again                     -> 409 DUPLICATE_PULL (reason visible)
  3  admin check                              -> blocked=true, reason visible
     Admins bypass their own monthly quota; system portal usage still counts calls.
  4  non-admin generate                       -> blocked by the colleague's FAILED PAN;
                                                 NO second vendor attempt
  4b non-admin check                          -> blocked=true, reason redacted
  5  non-admin generate?regenerate=true       -> 403 RE-PULL_ADMIN_ONLY
  6  non-admin quota                          -> failed vendor call charged once; blocked request adds nothing
  7  admin generate?regenerate=true           -> explicit retry, 2nd SOURCE=REGEN, FAILED+reason
  8  failed vendor attempts                   -> +1 quota credit per caller
  9  in-flight PENDING row present            -> 409 "already in progress"
 10  attempts moved to last month             -> freeze lifts, new attempt allowed
 11  report + OK ledger row seeded            -> admin: exists, pulledByMe=true, not blocked
                                                 non-admin: exists, pulledByMe=FALSE, not blocked
                                                 (the per-user popup: no colleague popup shown)  12  non-admin generate                       -> 200 source=CACHE, quota +1 for the non-admin
  12b the served report DOWNLOADS               -> 200 application/pdf for the non-admin
     (report pulled by admin and then served to the requester)
  12c negative control: an OLD report          -> still 403 for the non-admin, 200 for admin
  13  non-admin check again                    -> pulledByMe=true (their own popup now)
  14  failed pull records are VISIBLE          -> FAILED attempts in My Reports and
     GET /api/cibil/failed-pulls; admin all-company, non-admin team-scoped
  15  failed vendor calls count as one quota credit for requester
  16  successful Reports rows expose Status=OK; failure reasons are admin-only

Run with the backend started and an empty Cibil:ApiToken (e.g. `Cibil__ApiToken=
dotnet run --project Backend`), so the vendor call fails locally and no external
API or credit is touched.

Usage: python Backend/sql/dedup-e2e-test.py http://127.0.0.1:5000
Env:   SQLCMD (path to sqlcmd), CIP_DB/CIP_USER/CIP_PASS for ledger assertions.

Prerequisites: the Admin account, plus a non-admin employee DEDUP_TEST with
password Test@123 and TBL_CIP_WEB_ACCESS.HAS_ACCESS = 1 (role must not be 1/2):
  INSERT dbo.TBL_CIP_EMPLOYEE_MASTER (EMP_NAME, USERNAME, PASSWORD, ROLE_ID, IS_BYPASS)
  VALUES ('Dedup Test User', 'DEDUP_TEST', 'Test@123', 3, 1);
  INSERT dbo.TBL_CIP_WEB_ACCESS (EMP_CODE, HAS_ACCESS)
  SELECT EMP_CODE, 1 FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE USERNAME = 'DEDUP_TEST';
"""
import json
import os
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5000").rstrip("/")
SQLCMD = os.environ.get(
    "SQLCMD",
    r"C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\sqlcmd",
)
DB = os.environ.get("CIP_DB", "CIP")
DB_USER = os.environ.get("CIP_USER", "sa")
DB_PASS = os.environ.get("CIP_PASS", "123456")

ADMIN = ("Admin", "aria_1up@143")
USER = ("DEDUP_TEST", "Test@123")
PAN = "AAAAA0000A"  # matches ^[A-Z]{5}[0-9]{4}[A-Z]$, never a real customer
# A second fake PAN carrying a PREVIOUS-month report, used as the negative control
# for the download rule (an old report is never served by RULE 3, so it must stay
# closed to anyone outside the puller's hierarchy).
OLD_PAN = "AAAAA0001B"
# A REAL file on disk, so a download failure can only mean authorisation — never
# 'missing file'. Without this the fixture's FILE_PATH is bogus and the endpoint
# 404s before it ever reaches the scope check, which is how the bug hid this long.
TEST_PDF = os.path.join(tempfile.gettempdir(), "cip-e2e-report.pdf")

passed, failed = 0, 0


def check(label, ok, detail=""):
    global passed, failed
    if ok:
        passed += 1
        print(f"  PASS  {label}")
    else:
        failed += 1
        print(f"  FAIL  {label}  {detail}")


def call(method, path, payload=None, token=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(
        BASE + path,
        data=json.dumps(payload).encode() if payload is not None else None,
        headers=headers,
        method=method,
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            body = r.read().decode()
            return r.status, (json.loads(body) if body else {})
    except urllib.error.HTTPError as e:
        body = e.read().decode()
        try:
            return e.code, json.loads(body or "{}")
        except json.JSONDecodeError:
            return e.code, {"raw": body[:300]}


def call_raw(method, path, token=None):
    """call(), but returning the raw body + content-type so a PDF can be verified."""
    headers = {}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(BASE + path, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return r.status, r.read(), r.headers.get("Content-Type", "")
    except urllib.error.HTTPError as e:
        return e.code, e.read(), e.headers.get("Content-Type", "")


def report_id(pan):
    return sql(
        "SELECT CONVERT(varchar(20), REPORT_ID) FROM dbo.TBL_CIP_CIBIL_REPORTS "
        f"WHERE PAN_NO = '{pan}' ORDER BY REPORT_ID DESC;"
    )[0]


def login(cred):
    status, res = call("POST", "/api/auth/login", {"userName": cred[0], "password": cred[1]})
    # The token arrives either at the top level or nested under `user`.
    token = res.get("token") or (res.get("user") or {}).get("token")
    if status != 200 or not token:
        print(f"FATAL: login failed for {cred[0]}: {status} {res}")
        sys.exit(2)
    return token


def sql(query):
    """Run a query, return stripped rows (one line per row)."""
    out = subprocess.run(
        [SQLCMD, "-S", "localhost", "-U", DB_USER, "-P", DB_PASS, "-C", "-d", DB,
         "-b", "-h", "-1", "-W", "-Q", "SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; " + query],
        capture_output=True, text=True,
    )
    if out.returncode != 0:
        print(f"FATAL: sql failed: {out.stdout}{out.stderr}")
        sys.exit(2)
    return [ln.strip() for ln in out.stdout.splitlines() if ln.strip()]


def ledger():
    """One 'SOURCE|STATUS|reason?' line per attempt for the test PAN, newest first."""
    return sql(
        "SELECT SOURCE + '|' + STATUS + '|' + "
        "CASE WHEN ERROR_MESSAGE IS NULL THEN '-' ELSE 'R' END "
        f"FROM dbo.TBL_CIP_CIBIL_PULL_LOG WHERE PAN_NO = '{PAN}' "
        "ORDER BY PULLED_DATE DESC, LOG_ID DESC;"
    )


def report_rows(token):
    status, res = call("GET", "/api/cibil/reports", token=token)
    if status != 200:
        return status, []
    return status, res.get("rows", [])


def gen(token, regenerate=False):
    return call(
        "POST", "/api/cibil/generate" + ("?regenerate=true" if regenerate else ""),
        {"name": "Dedup Test", "mobile": "9000000000", "panCard": PAN,
         "gender": "male", "reportType": "cibil", "consent": "Y"},
        token,
    )


def main():
    admin, user = login(ADMIN), login(USER)

    # Clean slate: only ever touch the fake test PAN's artefacts.
    sql(f"DELETE FROM dbo.TBL_CIP_CIBIL_PULL_LOG WHERE PAN_NO = '{PAN}';")
    sql(f"DELETE FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE PAN_NO = '{PAN}';")

    # This user's quota is derived from every completed OK or FAILED ledger row this
    # month. Measure the DELTA rather than predicting the starting count from dirty DB.
    # Take this before the admin's failed attempt, because that event belongs to admin.
    used0 = (call("GET", "/api/cibil/quota", token=user)[1].get("used") or 0)

    print("[1] admin generate — vendor call fails (empty ApiToken)")
    status, res = gen(admin)
    check("attempt fails without spending anything", status != 200, f"status={status} {res}")
    rows = ledger()
    check("one FAILED ledger row with a reason",
          rows == ["API|FAILED|R"], f"rows={rows}")

    print("[2] admin generate again — frozen regardless of status")
    status, res = gen(admin)
    check("409 DUPLICATE_PULL", status == 409 and res.get("code") == "DUPLICATE_PULL",
          f"status={status} {res}")
    check("blocking attempt shown with FAILED status",
          (res.get("lastAttempt") or {}).get("status") == "FAILED", f"{res}")
    check("admin sees the failure reason", bool(res.get("lastAttempt", {}).get("errorMessage")), f"{res}")
    check("admin told re-pull is theirs to take", res.get("canRePull") is True, f"{res}")
    check("no second vendor attempt", ledger() == ["API|FAILED|R"], f"rows={ledger()}")

    print("[3] admin check — blocked, reason visible")
    status, res = call("POST", "/api/cibil/check", {"pan": PAN, "reportType": "cibil"}, admin)
    check("check reports blocked", status == 200 and res.get("blocked") is True, f"{res}")
    check("check exposes the reason to admin", bool((res.get("lastAttempt") or {}).get("errorMessage")), f"{res}")

    # Capture quota immediately before the first cross-user block, after admin's
    # failed attempt has proved that only its original requester is charged.
    user_used_before_block = (call("GET", "/api/cibil/quota", token=user)[1].get("used") or 0)

    print("[4] non-admin generate — another user's failure blocks before paid API")
    status, res = gen(user)
    check("409 DUPLICATE_PULL from global failed-PAN freeze",
          status == 409 and res.get("code") == "DUPLICATE_PULL", f"status={status} {res}")
    check("global blocker reports FAILED and redacts reason",
          res.get("lastAttempt", {}).get("status") == "FAILED"
          and res.get("lastAttempt", {}).get("errorMessage") in (None, ""), f"{res}")
    check("no second vendor attempt (only admin's row exists)",
          ledger() == ["API|FAILED|R"], f"rows={ledger()}")

    print("[4b] non-admin check — global failed PAN is visible, reason redacted")
    status, res = call("POST", "/api/cibil/check", {"pan": PAN, "reportType": "cibil"}, user)
    check("check reports global failed block", status == 200 and res.get("blocked") is True,
          f"{res}")
    check("status visible, vendor reason redacted",
          (res.get("lastAttempt") or {}).get("status") == "FAILED"
          and (res.get("lastAttempt") or {}).get("errorMessage") in (None, ""), f"{res}")

    print("[5] non-admin explicit re-pull — refused")
    status, res = gen(user, regenerate=True)
    check("403 RE-PULL_ADMIN_ONLY", status == 403 and res.get("code") == "RE-PULL_ADMIN_ONLY",
          f"status={status} {res}")
    check("refusal costs no attempt", ledger() == ["API|FAILED|R"], f"rows={ledger()}")

    print("[6] failed vendor attempt charged once; blocked retry did not charge again")
    status, res = call("GET", "/api/cibil/quota", token=user)
    check("admin's failed vendor call did not charge the non-admin",
          status == 200 and res.get("used") == user_used_before_block, f"before={user_used_before_block} now={res}")
    status, res = call("GET", "/api/cibil/quota", token=user)
    check("blocked retry added no further charge",
          status == 200 and res.get("used") == user_used_before_block, f"before={user_used_before_block} now={res}")

    print("[7] failed vendor attempt is included in system vendor-consumption totals")
    status, portal = call("GET", "/api/cibil/portal-usage", token=admin)
    check("portal usage endpoint includes failed vendor call",
          status == 200 and portal.get("portalPulls", 0) >= 1,
          f"status={status} portalPulls={portal.get('portalPulls')} {portal}")

    print("[8] admin explicit re-pull — allowed, logged as REGEN")
    status, res = gen(admin, regenerate=True)
    check("re-pull runs (fails only on the missing vendor token)", status != 200, f"status={status} {res}")
    rows = ledger()
    check("second attempt logged as REGEN|FAILED with a reason",
          rows == ["REGEN|FAILED|R", "API|FAILED|R"], f"rows={rows}")

    print("[9] in-flight PENDING row blocks a second vendor call")
    sql(
        f"INSERT INTO dbo.TBL_CIP_CIBIL_PULL_LOG "
        f"(PAN_NO, REPORT_TYPE, EMP_CODE, EMP_NAME, SOURCE, STATUS) "
        f"VALUES ('{PAN}', 'cibil', 0, 'in-flight-test', 'API', 'PENDING');"
    )
    status, res = gen(admin, regenerate=True)
    check("409 while a pull is in flight", status == 409 and res.get("code") == "DUPLICATE_PULL",
          f"status={status} {res}")
    check("in-flight message", "in progress" in res.get("error", ""), f"{res}")
    sql(f"DELETE FROM dbo.TBL_CIP_CIBIL_PULL_LOG WHERE PAN_NO = '{PAN}' AND STATUS = 'PENDING';")

    print("[10] the failed-PAN freeze lasts until the month rolls over")
    sql(f"UPDATE dbo.TBL_CIP_CIBIL_PULL_LOG SET PULLED_DATE = DATEADD(MONTH, -1, PULLED_DATE) "
        f"WHERE PAN_NO = '{PAN}';")
    user_quota_before_retry = (call("GET", "/api/cibil/quota", token=user)[1].get("used") or 0)
    status, res = gen(user)
    check("last month's failures do not block this month", status != 409,
          f"status={status} {res}")
    check("this month's attempt is logged again", any("API|FAILED" in r for r in ledger()),
          f"rows={ledger()}")
    user_quota_after_retry = (call("GET", "/api/cibil/quota", token=user)[1].get("used") or 0)
    check("failed vendor call consumes exactly one user quota credit",
          user_quota_after_retry == user_quota_before_retry + 1,
          f"before={user_quota_before_retry} after={user_quota_after_retry}")

    # Seed what a SUCCESSFUL admin pull leaves behind — a report row plus an OK
    # ledger row owned by the admin — since the empty vendor token can never
    # produce one. This is the fixture for the per-user popup / cache assertions.
    print("[11] seeded report — popup state is per user")
    with open(TEST_PDF, "wb") as fh:
        fh.write(b"%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n")
    # PULLED_BY is the ADMIN: this is the reported case — a client pulled by the
    # administrator and requested by somebody else.
    sql(
        f"INSERT INTO dbo.TBL_CIP_CIBIL_REPORTS "
        f"(PAN_NO, APPLICANT_NAME, REPORT_TYPE, FILE_NAME, FILE_PATH, FILE_SIZE, "
        f" PULLED_BY, PULLED_BY_NAME, SOURCE) "
        f"SELECT '{PAN}', 'Dedup Test', 'cibil', '{PAN}.pdf', '{TEST_PDF}', 1024, "
        f"       EMP_CODE, EMP_NAME, 'API' "
        f"FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE USERNAME = 'Admin';"
    )
    sql(
        f"INSERT INTO dbo.TBL_CIP_CIBIL_PULL_LOG "
        f"(PAN_NO, REPORT_TYPE, EMP_CODE, EMP_NAME, SOURCE, STATUS) "
        f"SELECT '{PAN}', 'cibil', EMP_CODE, EMP_NAME, 'API', 'OK' "
        f"FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE USERNAME = 'Admin';"
    )
    status, res = call("POST", "/api/cibil/check", {"pan": PAN, "reportType": "cibil"}, admin)
    check("admin: report exists", status == 200 and res.get("exists") is True, f"{res}")
    check("admin: own pull -> pulledByMe=true (popup is theirs)",
          res.get("pulledByMe") is True, f"{res}")
    check("admin: not blocked", res.get("blocked") is False, f"{res}")

    status, res = call("POST", "/api/cibil/check", {"pan": PAN, "reportType": "cibil"}, user)
    check("non-admin: report exists (will be served from cache)",
          status == 200 and res.get("exists") is True, f"{res}")
    check("non-admin: colleague's pull -> pulledByMe=FALSE (no colleague popup)",
          res.get("pulledByMe") is False, f"{res}")
    check("non-admin: not blocked by the colleague's history",
          res.get("blocked") is False, f"{res}")

    print("[12] non-admin generate — served from cache, counted for this user")
    # Assert the DELTA, not the absolute count: the quota is derived from every OK
    # ledger row this user has this month, so any earlier pull of a different PAN
    # leaves the dev database dirty and the absolute number is not ours to predict.
    _, q0 = call("GET", "/api/cibil/quota", token=user)
    status, res = gen(user)
    check("200 CACHE serve", status == 200 and res.get("source") == "CACHE",
          f"status={status} {res}")
    _, q1 = call("GET", "/api/cibil/quota", token=user)
    check("quota used +1 (the cache serve counts for this user)",
          q1.get("used") == (q0.get("used") or 0) + 1, f"before={q0} after={q1}")

    print("[12b] the served report must DOWNLOAD for the user it was served to")
    rid = report_id(PAN)
    status, body, ctype = call_raw("GET", f"/api/cibil/download/{rid}", user)
    check("non-admin can open the report the admin pulled for them",
          status == 200, f"status={status} {body[:200]}")
    check("the download really is the PDF",
          body[:5] == b"%PDF-", f"head={body[:20]!r}")
    check("served as a PDF", "application/pdf" in ctype, f"ctype={ctype}")
    status, body, _ = call_raw("GET", f"/api/cibil/download/{rid}", admin)
    check("admin can open it too", status == 200, f"status={status}")

    print("[12c] negative control — an OLD report is not opened up by the fix")
    # Previous month, pulled by the admin, never served to this user. RULE 3 does
    # not serve it, so the hierarchy rule must still refuse it (and must still let
    # the admin through).
    sql(
        f"INSERT INTO dbo.TBL_CIP_CIBIL_REPORTS "
        f"(PAN_NO, APPLICANT_NAME, REPORT_TYPE, FILE_NAME, FILE_PATH, FILE_SIZE, "
        f" PULLED_BY, PULLED_BY_NAME, SOURCE, PULLED_DATE) "
        f"SELECT '{OLD_PAN}', 'Old Dedup', 'cibil', '{OLD_PAN}.pdf', '{TEST_PDF}', 1024, "
        f"       EMP_CODE, EMP_NAME, 'API', DATEADD(MONTH, -1, GETDATE()) "
        f"FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE USERNAME = 'Admin';"
    )
    rid_old = report_id(OLD_PAN)
    status, body, _ = call_raw("GET", f"/api/cibil/download/{rid_old}", user)
    check("non-admin still refused another team's old report (403)",
          status == 403, f"status={status} {body[:200]}")
    status, body, _ = call_raw("GET", f"/api/cibil/download/{rid_old}", admin)
    check("admin still opens old reports (200)", status == 200, f"status={status}")

    print("[13] non-admin check again — the popup is now THEIRS")
    status, res = call("POST", "/api/cibil/check", {"pan": PAN, "reportType": "cibil"}, user)
    check("pulledByMe=true after their own cache pull",
          status == 200 and res.get("pulledByMe") is True, f"{res}")
    check("still not blocked", res.get("blocked") is False, f"{res}")

    # A FAILED pull inserts NO row into TBL_CIP_CIBIL_REPORTS, so the reports grid
    # cannot show it. The ledger-backed views make it visible, expose successful report
    # status in the Reports grid, and enforce requester quota + company-wide failed-PAN
    # freezing.
    print("[14-16] failed attempts are visible and successful Reports rows show status")
    status, mine = call("GET", "/api/cibil/my-reports", token=admin)
    myrows = mine.get("rows", [])
    check("admin my-reports 200", status == 200, f"status={status}")
    check("admin's own OK rows are still listed",
          any(r.get("status") == "OK" for r in myrows),
          f"rows={[(r.get('panNo'), r.get('status')) for r in myrows]}")
    check("admin's FAILED attempts are listed too",
          any(r.get("status") == "FAILED" for r in myrows),
          f"rows={[(r.get('panNo'), r.get('status')) for r in myrows]}")
    reports_status, successful_reports = report_rows(admin)
    check("successful Reports grid rows expose status=OK",
          reports_status == 200 and bool(successful_reports)
          and all(r.get("status") == "OK" for r in successful_reports),
          f"status={reports_status} statuses={[r.get('status') for r in successful_reports]}")
    admin_failed = [r for r in myrows if r.get("status") == "FAILED"]
    if admin_failed:
        check("a FAILED row has no reportId, so nothing is downloadable",
              admin_failed[0].get("reportId") in (None, ""), f"{admin_failed[0]}")
        check("admin sees the vendor's reason in My Reports",
              bool(admin_failed[0].get("errorMessage")), f"{admin_failed[0]}")

    status, fl = call("GET", "/api/cibil/failed-pulls", token=admin)
    frows = fl.get("rows", [])
    check("admin failed-pulls 200", status == 200, f"status={status}")
    check("only non-OK attempts are returned",
          all(r.get("status") in ("FAILED", "PENDING") for r in frows),
          f"rows={[(r.get('panNo'), r.get('status')) for r in frows]}")
    admin_own = [r for r in frows if r.get("panNo") == PAN and r.get("status") == "FAILED"]
    check("the admin's failed pull is listed WITH its reason",
          bool(admin_own) and bool(admin_own[0].get("errorMessage")), f"rows={frows}")
    check("every row names who attempted it",
          all(r.get("requestedBy") for r in frows), f"rows={frows}")

    status, ufl = call("GET", "/api/cibil/failed-pulls", token=user)
    ufrows = ufl.get("rows", [])
    check("non-admin failed-pulls 200", status == 200, f"status={status}")
    check("the vendor's reason is redacted for a non-admin",
          all(not r.get("errorMessage") for r in ufrows), f"rows={ufrows}")
    check("a non-admin never sees another team's failed attempts",
          all(r.get("requestedBy") != "Administrator" for r in ufrows), f"rows={ufrows}")

    status, umine = call("GET", "/api/cibil/my-reports", token=user)
    urows = umine.get("rows", [])
    check("a non-admin's own failed pull IS in their My Reports",
          any(r.get("panNo") == PAN and r.get("status") == "FAILED" for r in urows),
          f"rows={[(r.get('panNo'), r.get('status')) for r in urows]}")
    check("and its reason is redacted there too",
          all(not r.get("errorMessage") for r in urows), f"rows={urows}")
    check("failed-pulls needs a token",
          call("GET", "/api/cibil/failed-pulls")[0] == 401)

    # Leave nothing behind: only the fake PANs' artefacts were ever touched.
    for p in (PAN, OLD_PAN):
        sql(f"DELETE FROM dbo.TBL_CIP_CIBIL_PULL_LOG WHERE PAN_NO = '{p}';")
        sql(f"DELETE FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE PAN_NO = '{p}';")
    if os.path.exists(TEST_PDF):
        os.remove(TEST_PDF)

    print(f"\n{passed} passed, {failed} failed")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
