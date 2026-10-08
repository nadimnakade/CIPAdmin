#!/usr/bin/env python3
"""
End-to-end check of the CIBIL duplicate-pull freeze against a RUNNING backend.

The rule under test: duplicate-pull state is scoped PER USER. A PAN that YOU
have attempted this calendar month is frozen for YOU, whatever the attempt's
outcome (OK / FAILED / in-flight PENDING); a colleague's attempt never blocks
you. A colleague's successful pull is served to you from cache and counts one
pull against YOUR quota. Only an in-flight PENDING pull by anyone still blocks
everyone (shared vendor credits / UX_CIP_PULL_INFLIGHT). An admin re-pull
(regenerate=true) may go past the freeze, and the vendor's failure reason is
disclosed to admins only.

  1  admin generate, vendor call fails        -> FAILED row + reason logged
  2  admin generate again                     -> 409 DUPLICATE_PULL (reason visible)
  3  admin check                              -> blocked=true, reason visible
  4  non-admin generate                       -> NOT blocked by the colleague's failure;
                                                 its own attempt fails on the token
  4b non-admin generate again                 -> 409 DUPLICATE_PULL (own attempt), reason redacted
  5  non-admin generate?regenerate=true       -> 403 RE-PULL_ADMIN_ONLY
  6  non-admin check                          -> blocked=true, no reason (own failure)
  7  non-admin quota                          -> FAILED attempts cost no allowance
  8  admin generate?regenerate=true           -> 2nd attempt SOURCE=REGEN, FAILED+reason
  9  in-flight PENDING row present            -> 409 "already in progress"
 10  attempts moved to last month             -> freeze lifts, new attempt allowed
 11  report + OK ledger row seeded            -> admin: exists, pulledByMe=true, not blocked
                                                 non-admin: exists, pulledByMe=FALSE, not blocked
                                                 (the per-user popup: no colleague popup shown)
 12  non-admin generate                       -> 200 source=CACHE, quota +1 for the non-admin
 13  non-admin check again                    -> pulledByMe=true (their own popup now)

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

    print("[4] non-admin generate — a colleague's failure does NOT block this user")
    status, res = gen(user)
    check("colleague's FAILED attempt does not freeze the PAN for this user",
          status != 409, f"status={status} {res}")
    check("the user's own attempt runs (and fails on the empty vendor token)",
          status != 200, f"status={status} {res}")
    rows = ledger()
    check("two FAILED rows now — one per user",
          rows == ["API|FAILED|R", "API|FAILED|R"], f"rows={rows}")

    print("[4b] non-admin generate again — frozen by their OWN attempt")
    status, res = gen(user)
    check("409 DUPLICATE_PULL", status == 409 and res.get("code") == "DUPLICATE_PULL",
          f"status={status} {res}")
    check("failure reason redacted", res.get("lastAttempt", {}).get("errorMessage") in (None, ""), f"{res}")
    check("non-admin told only admin can re-pull", res.get("canRePull") is False, f"{res}")
    check("no third vendor attempt", ledger() == ["API|FAILED|R", "API|FAILED|R"],
          f"rows={ledger()}")

    print("[5] non-admin re-pull — refused")
    status, res = gen(user, regenerate=True)
    check("403 RE-PULL_ADMIN_ONLY", status == 403 and res.get("code") == "RE-PULL_ADMIN_ONLY",
          f"status={status} {res}")
    check("refusal costs no attempt", ledger() == ["API|FAILED|R", "API|FAILED|R"],
          f"rows={ledger()}")

    print("[6] non-admin check — blocked, no reason")
    status, res = call("POST", "/api/cibil/check", {"pan": PAN, "reportType": "cibil"}, user)
    check("check reports blocked", status == 200 and res.get("blocked") is True, f"{res}")
    check("check reason redacted", (res.get("lastAttempt") or {}).get("errorMessage") in (None, ""), f"{res}")

    print("[7] failed attempts cost no monthly allowance")
    status, res = call("GET", "/api/cibil/quota", token=user)
    check("quota used == 0", status == 200 and res.get("used") == 0, f"{res}")

    print("[8] admin re-pull — allowed, logged as REGEN")
    status, res = gen(admin, regenerate=True)
    check("re-pull runs (fails only on the missing vendor token)", status != 200, f"status={status} {res}")
    rows = ledger()
    check("second attempt logged as REGEN|FAILED with a reason",
          rows == ["REGEN|FAILED|R", "API|FAILED|R", "API|FAILED|R"], f"rows={rows}")

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

    print("[10] the freeze lasts until the month rolls over")
    sql(f"UPDATE dbo.TBL_CIP_CIBIL_PULL_LOG SET PULLED_DATE = DATEADD(MONTH, -1, PULLED_DATE) "
        f"WHERE PAN_NO = '{PAN}';")
    status, res = gen(user)
    check("last month's failures do not block this month", status != 409,
          f"status={status} {res}")
    check("this month's attempt is logged again", any("|FAILED" in r or "FAILED" in r for r in ledger()),
          f"rows={ledger()}")

    # Seed what a SUCCESSFUL admin pull leaves behind — a report row plus an OK
    # ledger row owned by the admin — since the empty vendor token can never
    # produce one. This is the fixture for the per-user popup / cache assertions.
    print("[11] seeded report — popup state is per user")
    sql(
        f"INSERT INTO dbo.TBL_CIP_CIBIL_REPORTS "
        f"(PAN_NO, APPLICANT_NAME, REPORT_TYPE, FILE_NAME, FILE_PATH, FILE_SIZE, "
        f" PULLED_BY_NAME, SOURCE) "
        f"VALUES ('{PAN}', 'Dedup Test', 'cibil', '{PAN}.pdf', "
        f"'seeded-by-e2e-{PAN}.pdf', 1024, 'Admin', 'API');"
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
    status, res = gen(user)
    check("200 CACHE serve", status == 200 and res.get("source") == "CACHE",
          f"status={status} {res}")
    status, res = call("GET", "/api/cibil/quota", token=user)
    check("quota used == 1 (the cache serve counts for this user)",
          status == 200 and res.get("used") == 1, f"{res}")

    print("[13] non-admin check again — the popup is now THEIRS")
    status, res = call("POST", "/api/cibil/check", {"pan": PAN, "reportType": "cibil"}, user)
    check("pulledByMe=true after their own cache pull",
          status == 200 and res.get("pulledByMe") is True, f"{res}")
    check("still not blocked", res.get("blocked") is False, f"{res}")

    # Leave nothing behind: only the fake PAN's artefacts were ever touched.
    sql(f"DELETE FROM dbo.TBL_CIP_CIBIL_PULL_LOG WHERE PAN_NO = '{PAN}';")
    sql(f"DELETE FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE PAN_NO = '{PAN}';")

    print(f"\n{passed} passed, {failed} failed")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
