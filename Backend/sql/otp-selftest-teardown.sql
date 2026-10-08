/* =============================================================================
   CIP Admin - authenticator test fixture teardown.

   Removes everything otp-selftest.sql creates. It is a SEPARATE script on purpose:
   sqlcmd cannot express an optional variable - referencing an undefined $(Var)
   aborts the whole script, and a :setvar inside a file always overrides a
   command-line -v, so a single script could never be made to branch both ways.

   Run:  sqlcmd -S tcp:localhost,1433 -d CIP -U sa -P **** -C -b -h -1 -W -l 30 ^
                -i Backend/sql/otp-selftest-teardown.sql
============================================================================= */

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @Users TABLE (USERNAME VARCHAR(50));
INSERT INTO @Users (USERNAME) VALUES ('OTP_BYPASS_TEST'), ('OTP_ENROLLED_TEST'), ('OTP_FRESH_TEST');

DELETE FROM dbo.TBL_CIP_WEB_ACCESS
 WHERE EMP_CODE IN (SELECT EMP_CODE FROM dbo.TBL_CIP_EMPLOYEE_MASTER
                     WHERE USERNAME IN (SELECT USERNAME FROM @Users));

DELETE FROM dbo.TBL_CIP_ACTIVE_SESSION
 WHERE EMP_CODE IN (SELECT EMP_CODE FROM dbo.TBL_CIP_EMPLOYEE_MASTER
                     WHERE USERNAME IN (SELECT USERNAME FROM @Users));

DELETE FROM dbo.TBL_CIP_LOGIN_LOGS
 WHERE USERNAME IN (SELECT USERNAME FROM @Users);

DELETE FROM dbo.TBL_CIP_EMPLOYEE_MASTER
 WHERE USERNAME IN (SELECT USERNAME FROM @Users);

-- Only the role this fixture created, matched by name as well as id.
DELETE FROM dbo.TBL_CIP_ROLE_MENU WHERE MENU_ID NOT IN (SELECT MENU_ID FROM dbo.TBL_CIP_MENU_MASTER);
DELETE FROM dbo.TBL_CIP_ROLE_MASTER WHERE ROLE_ID = 999 AND ROLE_NAME = 'OTP TEST ROLE';

/* Verification: nothing from the fixture is left behind. */
SELECT 'employees' AS OBJECT, COUNT(*) AS ROWS_LEFT FROM dbo.TBL_CIP_EMPLOYEE_MASTER
 WHERE USERNAME IN (SELECT USERNAME FROM @Users)
UNION ALL
SELECT 'role', COUNT(*) FROM dbo.TBL_CIP_ROLE_MASTER WHERE ROLE_ID = 999
UNION ALL
SELECT 'login logs', COUNT(*) FROM dbo.TBL_CIP_LOGIN_LOGS
 WHERE USERNAME IN (SELECT USERNAME FROM @Users);
