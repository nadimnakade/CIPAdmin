/* =============================================================================
   CIP Admin - deployment pre-flight check (READ-ONLY)

   Run this against the PRODUCTION CIP database BEFORE copying the new package.
   It changes nothing: every statement is a SELECT. Each row answers one
   question - status MISSING means the migration named in the "run this" column
   has to be applied first.

   Usage:
     sqlcmd -S tcp:YOUR-SQL-HOST,1433 -d CIP -U sa -P **** -C -b -h -1 -W ^
            -i Backend/sql/DEPLOY-CHECK.sql

   The -b flag matters: without it sqlcmd exits 0 even when a statement fails.
   Safe to run as often as you like; it never writes.
============================================================================= */

SET NOCOUNT ON;

/* ---------- 1. Tables this build expects ---------- */
SELECT 'TABLE      ' + t.name AS item,
       CASE WHEN OBJECT_ID('dbo.' + t.name, 'U') IS NULL THEN 'MISSING' ELSE 'present' END AS status,
       t.run_this AS action
FROM (VALUES
    ('TBL_CIP_ACTIVE_SESSION',    'Backend/sql/USER-SINGLE-SESSION.sql'),
    ('TBL_CIP_APP_SETTINGS',      'Backend/sql/TBL_CIP_APP_SETTINGS.sql'),
    ('TBL_CIP_CIBIL_PULL_LOG',    'Backend/sql/CIBIL-PULL-LOG.sql'),
    ('TBL_CIP_MENU_MASTER',       'Database/schema.sql'),
    ('TBL_CIP_ROLE_MENU',         'Database/schema.sql'),
    ('TBL_CIP_ROLE_MASTER',       'Database/schema.sql'),
    ('TBL_CIP_EMPLOYEE_MASTER',   'Database/schema.sql'),
    ('TBL_CIP_WEB_ACCESS',        'Database/schema.sql'),
    ('TBL_CIP_CIBIL_REPORTS',     'Database/schema.sql'),
    ('TBL_CIP_OCR_JOBS',          'Database/schema.sql'),
    ('TBL_CIP_ELIGIBILITY_CONFIG','pre-existing (legacy CIP schema)')
) AS t(name, run_this)
ORDER BY CASE WHEN OBJECT_ID('dbo.' + t.name, 'U') IS NULL THEN 0 ELSE 1 END, t.name;

/* ---------- 2. Menus added by this build ---------- */
SELECT 'MENU       ' + want.MENU_LABEL AS item,
       CASE WHEN m.MENU_ID IS NULL THEN 'MISSING' ELSE 'present' END AS status,
       CASE
           WHEN m.MENU_ID IS NULL AND want.MENU_PATH IN ('/loan-eligibility','/emi-calculator','/applications','/applications/new','/applications/scans','/applications/bulk','/applications/existing-loans')
               THEN 'Backend/sql/APPLICATION-MODULES-MENU.sql'
           WHEN m.MENU_ID IS NULL AND want.MENU_PATH = '/punch-in'
               THEN 'Backend/sql/PUNCHIN-MENU.sql'
           WHEN m.MENU_ID IS NULL AND want.MENU_PATH = '/financial-analysis'
               THEN 'Backend/sql/FINANCIAL-ANALYSIS-MENU.sql'
           WHEN m.MENU_ID IS NULL AND want.MENU_LABEL LIKE 'New Tax Regime%'
               THEN 'Backend/sql/NEW-TAX-REGIME-MENU.sql'
           WHEN m.MENU_ID IS NULL AND want.MENU_PATH = '/authenticator-setup'
               THEN 'Backend/sql/AUTHENTICATOR-ADMIN-MENU.sql'
           ELSE '-'
       END AS action
FROM (VALUES
    ('Applications',              CAST(NULL AS VARCHAR(200))),
    ('Loan Eligibility',          '/loan-eligibility'),
    ('EMI Calculator',            '/emi-calculator'),
    ('Search Application',        '/applications'),
    ('New Application',           '/applications/new'),
    ('Scan File Upload',          '/applications/scans'),
    ('Bulk Insert',               '/applications/bulk'),
    ('Existing Loans',            '/applications/existing-loans'),
    ('Punch In / Out',            '/punch-in'),
    ('Financial Analysis',        '/financial-analysis'),
    ('Authenticator Setup',       '/authenticator-setup'),
    ('New Tax Regime Calculator', 'https://cleartax.in/paytax/')
) AS want(MENU_LABEL, MENU_PATH)
LEFT JOIN dbo.TBL_CIP_MENU_MASTER m
       ON m.MENU_LABEL = want.MENU_LABEL
      AND ISNULL(m.MENU_PATH, '') = ISNULL(want.MENU_PATH, '')
ORDER BY CASE WHEN m.MENU_ID IS NULL THEN 0 ELSE 1 END, want.MENU_LABEL;

/* ---------- 3. Role grants for the two new sections ----------
   A section with no grant is invisible to every role except the ones that bypass
   the menu, so the menu would exist but never appear. The expected grants below
   mirror the migrations EXACTLY - do not widen this to "every active role" or a
   correctly migrated database will report a wall of false MISSING rows:

     APPLICATION-MODULES-MENU.sql : 2001, 2002 -> every active role (read-only
                                    reference screens)
                                  : 2000, 2003-2007 -> SUPER ADMIN, ADMIN, OPS, TL,
                                    DATA ENTRY, CIP ENTRY
     PUNCHIN-MENU.sql            : 3000, 3001 -> SUPER ADMIN, ADMIN, OPS, TL,
                                    PICKUP ADMIN, TL-OPS, HL-BL-OPS, SUBBROKER, HR-OPS

   Menus are resolved by label, never by hard-coded id. A blank result set is the
   expected outcome on a fully migrated database.                                */
DECLARE @Expect TABLE (MENU_LABEL VARCHAR(100), MENU_PATH VARCHAR(200), ROLE_ID INT, ALL_ROLES BIT);

INSERT INTO @Expect (MENU_LABEL, MENU_PATH, ROLE_ID, ALL_ROLES)
-- Read-only reference screens: every active role.
SELECT v.LABEL, v.PATH, r.ROLE_ID, 1
  FROM (VALUES ('Loan Eligibility', '/loan-eligibility'),
               ('EMI Calculator',   '/emi-calculator')) AS v(LABEL, PATH)
 CROSS JOIN dbo.TBL_CIP_ROLE_MASTER r
 WHERE ISNULL(r.ROLE_ACTIVE, 1) = 1;

-- Application entry screens: the roles that actually key in applications.
INSERT INTO @Expect (MENU_LABEL, MENU_PATH, ROLE_ID, ALL_ROLES)
SELECT v.LABEL, v.PATH, a.ROLE_ID, 0
  FROM (VALUES ('Applications',       CAST(NULL AS VARCHAR(200))),
               ('Search Application', '/applications'),
               ('New Application',    '/applications/new'),
               ('Scan File Upload',   '/applications/scans'),
               ('Bulk Insert',        '/applications/bulk'),
               ('Existing Loans',     '/applications/existing-loans')) AS v(LABEL, PATH)
 CROSS JOIN (VALUES (1), (2), (3), (4), (1008), (2012)) AS a(ROLE_ID);

-- Attendance: the field-supervisor roles.
INSERT INTO @Expect (MENU_LABEL, MENU_PATH, ROLE_ID, ALL_ROLES)
SELECT v.LABEL, v.PATH, a.ROLE_ID, 0
  FROM (VALUES ('Attendance',    CAST(NULL AS VARCHAR(200))),
               ('Punch In / Out', '/punch-in')) AS v(LABEL, PATH)
 CROSS JOIN (VALUES (1), (2), (3), (4), (1006), (1007), (1009), (2010), (2011)) AS a(ROLE_ID);

SELECT 'GRANT      ' + e.MENU_LABEL + ' -> ' + ISNULL(r.ROLE_NAME, 'role ' + CAST(e.ROLE_ID AS VARCHAR(20))) AS item,
       'MISSING' AS status,
       CASE WHEN m.MENU_ID IS NULL
            THEN 'menu row itself is missing - run the matching menu script first'
            WHEN r.ROLE_ID IS NULL
            THEN 'role ' + CAST(e.ROLE_ID AS VARCHAR(20)) + ' is not in TBL_CIP_ROLE_MASTER'
            ELSE 're-run ' + CASE WHEN e.MENU_LABEL = 'Punch In / Out' OR e.MENU_LABEL = 'Attendance'
                                 THEN 'Backend/sql/PUNCHIN-MENU.sql'
                                 ELSE 'Backend/sql/APPLICATION-MODULES-MENU.sql' END
       END AS action
FROM @Expect e
LEFT JOIN dbo.TBL_CIP_MENU_MASTER m
       ON m.MENU_LABEL = e.MENU_LABEL
      AND ISNULL(m.MENU_PATH, '') = ISNULL(e.MENU_PATH, '')
LEFT JOIN dbo.TBL_CIP_ROLE_MASTER r
       ON r.ROLE_ID = e.ROLE_ID
      AND (e.ALL_ROLES = 1 OR ISNULL(r.ROLE_ACTIVE, 1) = 1)
WHERE m.MENU_ID IS NULL
   OR r.ROLE_ID IS NULL
   OR NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU rm
                   WHERE rm.ROLE_ID = e.ROLE_ID AND rm.MENU_ID = m.MENU_ID)
ORDER BY e.MENU_LABEL, item;

SELECT 'GRANTS OK  ' + CAST(COUNT(*) AS VARCHAR(20)) + ' of ' +
       CAST((SELECT COUNT(*) FROM @Expect) AS VARCHAR(20)) + ' expected menu/role pairs present' AS item,
       CASE WHEN COUNT(*) = (SELECT COUNT(*) FROM @Expect) THEN 'present' ELSE 'INCOMPLETE' END AS status,
       'no output above this line means the two new sections are fully granted' AS action
FROM @Expect e
JOIN dbo.TBL_CIP_MENU_MASTER m
  ON m.MENU_LABEL = e.MENU_LABEL
 AND ISNULL(m.MENU_PATH, '') = ISNULL(e.MENU_PATH, '')
WHERE EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MASTER r
               WHERE r.ROLE_ID = e.ROLE_ID
                 AND (e.ALL_ROLES = 1 OR ISNULL(r.ROLE_ACTIVE, 1) = 1))
  AND EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU rm
               WHERE rm.ROLE_ID = e.ROLE_ID AND rm.MENU_ID = m.MENU_ID);

/* ---------- 4. Stored procedures the new modules call ---------- */
SELECT 'PROC       ' + p.name AS item,
       CASE WHEN OBJECT_ID('dbo.' + p.name, 'P') IS NULL THEN 'MISSING' ELSE 'present' END AS status,
       'Legacy CIP object - restore from a CIP backup if missing' AS action
FROM (VALUES
    ('USP_CIP_LOAN_ELIGIBILITY'),
    ('USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING'),
    ('USP_CIP_DATA_ENTRY'),
    ('USP_CIP_FILL_DROPDOWN'),
    ('USP_CIP_EXISTINGLOAN_DETAILS'),
    ('USP_CIP_DELETE_APPLICATION'),
    ('USP_MANAGE_PUNCHINOUT_TEST'),
    ('USP_EMPLOYEE_NOT_PUNCHED_TODAY'),
    ('USP_CIP_FILL_SUPERVISOR')
) AS p(name)
ORDER BY CASE WHEN OBJECT_ID('dbo.' + p.name, 'P') IS NULL THEN 0 ELSE 1 END, p.name;

/* ---------- 5. Authenticator (Google Authenticator) columns ----------
   The second factor needs all three columns on TBL_CIP_EMPLOYEE_MASTER. Without
   IS_BYPASS the login cannot tell "skip the authenticator" from "require it". */
SELECT 'OTP COLUMN ' + want.COLUMN_NAME AS item,
       CASE WHEN c.COLUMN_NAME IS NULL THEN 'MISSING' ELSE 'present' END AS status,
       'pre-existing (legacy CIP schema)' AS action
FROM (VALUES
    ('IS_BYPASS'),                   -- 1 = authenticator skipped, 0 = required
    ('IS_GOOGLE_AUTHENTICATED'),     -- 1 = already enrolled
    ('GOOGLE_AUTH_SECRET_KEY')       -- Base32 TOTP secret
) AS want(COLUMN_NAME)
LEFT JOIN INFORMATION_SCHEMA.COLUMNS c
       ON c.TABLE_NAME = 'TBL_CIP_EMPLOYEE_MASTER'
      AND c.COLUMN_NAME = want.COLUMN_NAME
ORDER BY CASE WHEN c.COLUMN_NAME IS NULL THEN 0 ELSE 1 END, want.COLUMN_NAME;

/* ---------- 6. Authenticator rollout posture ----------
   Informational, not a failure: with every employee on IS_BYPASS = 1 the
   authenticator is dormant, which is the safe default but is rarely the intent
   on a production rollout. */
SELECT 'OTP USAGE  ' + CAST(SUM(CASE WHEN ISNULL(IS_BYPASS, 0) = 1 THEN 1 ELSE 0 END) AS VARCHAR(20)) +
       ' of ' + CAST(COUNT(*) AS VARCHAR(20)) + ' employees skip the authenticator' AS item,
       CASE WHEN SUM(CASE WHEN ISNULL(IS_BYPASS, 0) = 0 THEN 1 ELSE 0 END) = 0
            THEN 'dormant' ELSE 'active' END AS status,
       CASE WHEN SUM(CASE WHEN ISNULL(IS_BYPASS, 0) = 0 THEN 1 ELSE 0 END) = 0
            THEN 'set IS_BYPASS = 0 per employee on the Authenticator Setup page'
            ELSE 'authenticator is required for the employees counted as IS_BYPASS = 0' END AS action
  FROM dbo.TBL_CIP_EMPLOYEE_MASTER
 WHERE ISNULL(IS_DELETED, 0) = 0;

/* ---------- 7. Summary ---------- */
SELECT '--- run every migration whose row above says MISSING, then re-run this file ---' AS item,
       'next' AS status,
       'ORDER: schema.sql -> USER-SINGLE-SESSION -> TBL_CIP_APP_SETTINGS -> CIBIL-PULL-LOG -> menu scripts' AS action;