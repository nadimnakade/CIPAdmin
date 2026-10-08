/* =============================================================================
   CIP Admin - authenticator (Google Authenticator) end-to-end test fixture.

   Creates three throwaway employees that cover every branch of the second-factor
   rule, so the flow can be exercised without touching real staff:

     OTP_BYPASS_TEST    IS_BYPASS=1                 -> straight to the dashboard
     OTP_ENROLLED_TEST  IS_BYPASS=0, enrolled      -> asks for a 6-digit code
     OTP_FRESH_TEST     IS_BYPASS=0, not enrolled  -> shows the QR code

   All three get an explicit TBL_CIP_WEB_ACCESS grant, because role 999 is not one
   of the default-allowed roles (1/2/4).

   Safe to re-run: each pass re-arms the intended state, so a previous run that
   enrolled OTP_FRESH_TEST is undone. Pass 0 to remove all three again:

     sqlcmd ... -i Backend/sql/otp-selftest.sql
     sqlcmd ... -v Remove=1 -i Backend/sql/otp-selftest.sql

   Run:  sqlcmd -S tcp:localhost,1433 -d CIP -U sa -P **** -C -b -h -1 -W -l 30 ^
                -i Backend/sql/otp-selftest.sql
============================================================================= */

SET NOCOUNT ON;
-- sqlcmd connects with QUOTED_IDENTIFIER OFF by default and the inserts below fail
-- with "SET options have incorrect settings" without this.
SET QUOTED_IDENTIFIER ON;

DECLARE @TestRole INT = 999;
DECLARE @Pwd VARCHAR(50) = 'OtpTest@123';

/* How to tear the fixture down again.

   sqlcmd has no "this variable is optional" mechanism: referencing $(Remove) when it
   was never defined aborts the whole script, and a :setvar inside the file always
   wins over a command-line -v. So the teardown lives in its own script instead —
   see otp-selftest-teardown.sql. This file only ever creates/re-arms the fixture. */

/* ---------- teardown ---------- */
/* ---------- role ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MASTER WHERE ROLE_ID = @TestRole)
BEGIN
    SET IDENTITY_INSERT dbo.TBL_CIP_ROLE_MASTER ON;
    INSERT INTO dbo.TBL_CIP_ROLE_MASTER (ROLE_ID, ROLE_NAME, ROLE_READ, ROLE_WRITE, ROLE_ACTIVE)
    VALUES (@TestRole, 'OTP TEST ROLE', 1, 1, 1);
    SET IDENTITY_INSERT dbo.TBL_CIP_ROLE_MASTER OFF;
END

/* ---------- OTP_BYPASS_TEST: IS_BYPASS = 1, no authenticator ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE USERNAME = 'OTP_BYPASS_TEST')
    INSERT INTO dbo.TBL_CIP_EMPLOYEE_MASTER
        (EMP_NAME, USERNAME, PASSWORD, ROLE_ID, IS_BYPASS, IS_GOOGLE_AUTHENTICATED, GOOGLE_AUTH_SECRET_KEY)
    VALUES ('OTP Bypass Test', 'OTP_BYPASS_TEST', @Pwd, @TestRole, 1, 0, NULL);

UPDATE dbo.TBL_CIP_EMPLOYEE_MASTER
   SET IS_BYPASS = 1, IS_GOOGLE_AUTHENTICATED = 0, GOOGLE_AUTH_SECRET_KEY = NULL,
       PASSWORD = @Pwd, ROLE_ID = @TestRole, IS_DELETED = 0
 WHERE USERNAME = 'OTP_BYPASS_TEST';

/* ---------- OTP_ENROLLED_TEST: IS_BYPASS = 0, already enrolled ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE USERNAME = 'OTP_ENROLLED_TEST')
    INSERT INTO dbo.TBL_CIP_EMPLOYEE_MASTER
        (EMP_NAME, USERNAME, PASSWORD, ROLE_ID, IS_BYPASS, IS_GOOGLE_AUTHENTICATED, GOOGLE_AUTH_SECRET_KEY)
    VALUES ('OTP Enrolled Test', 'OTP_ENROLLED_TEST', @Pwd, @TestRole, 0, 1, 'JBSWY3DPEHPK3PXP');

UPDATE dbo.TBL_CIP_EMPLOYEE_MASTER
   SET IS_BYPASS = 0, IS_GOOGLE_AUTHENTICATED = 1, GOOGLE_AUTH_SECRET_KEY = 'JBSWY3DPEHPK3PXP',
       PASSWORD = @Pwd, ROLE_ID = @TestRole, IS_DELETED = 0
 WHERE USERNAME = 'OTP_ENROLLED_TEST';

/* ---------- OTP_FRESH_TEST: IS_BYPASS = 0, never enrolled (shows the QR) ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE USERNAME = 'OTP_FRESH_TEST')
    INSERT INTO dbo.TBL_CIP_EMPLOYEE_MASTER
        (EMP_NAME, USERNAME, PASSWORD, ROLE_ID, IS_BYPASS, IS_GOOGLE_AUTHENTICATED, GOOGLE_AUTH_SECRET_KEY)
    VALUES ('OTP Fresh Test', 'OTP_FRESH_TEST', @Pwd, @TestRole, 0, 0, NULL);

UPDATE dbo.TBL_CIP_EMPLOYEE_MASTER
   SET IS_BYPASS = 0, IS_GOOGLE_AUTHENTICATED = 0, GOOGLE_AUTH_SECRET_KEY = NULL,
       PASSWORD = @Pwd, ROLE_ID = @TestRole, IS_DELETED = 0
 WHERE USERNAME = 'OTP_FRESH_TEST';

/* ---------- explicit web access for all three ---------- */
INSERT INTO dbo.TBL_CIP_WEB_ACCESS (EMP_CODE, HAS_ACCESS)
SELECT e.EMP_CODE, 1
  FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
 WHERE e.USERNAME IN ('OTP_BYPASS_TEST','OTP_ENROLLED_TEST','OTP_FRESH_TEST')
   AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_WEB_ACCESS w WHERE w.EMP_CODE = e.EMP_CODE);

/* ---------- clear any throttling left over from an earlier test run ---------- */
DELETE FROM dbo.TBL_CIP_LOGIN_LOGS
 WHERE USERNAME IN ('OTP_BYPASS_TEST','OTP_ENROLLED_TEST','OTP_FRESH_TEST');

/* Verification: the three rows and the branch each one should exercise. */
SELECT USERNAME, EMP_CODE, IS_BYPASS, IS_GOOGLE_AUTHENTICATED,
       CASE WHEN GOOGLE_AUTH_SECRET_KEY IS NULL THEN 'no secret' ELSE 'has secret' END AS SECRET_STATE,
       CASE WHEN IS_BYPASS = 1 THEN 'expect: straight to dashboard'
            WHEN IS_GOOGLE_AUTHENTICATED = 1 THEN 'expect: ask for 6-digit code'
            ELSE 'expect: show QR code' END AS EXPECTED_BEHAVIOUR
  FROM dbo.TBL_CIP_EMPLOYEE_MASTER
 WHERE USERNAME IN ('OTP_BYPASS_TEST','OTP_ENROLLED_TEST','OTP_FRESH_TEST')
 ORDER BY USERNAME;
