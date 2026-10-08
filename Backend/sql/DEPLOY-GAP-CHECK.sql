/* ===========================================================================
   DEPLOY-GAP-CHECK.sql  -- read-only. What has NOT been deployed yet?

   WHY THIS EXISTS
   ---------------
   Publishing the app folder is only half a deployment. Each feature also needs a
   script from Backend/sql/ to be run against the server database. An app can be
   live and working while its table, menu row or unique index is still missing,
   and the symptom is confusing: the page loads and then silently does nothing.

   The API makes this easy to miss, because MapFallbackToFile("index.html")
   answers ANY unmatched GET with the SPA and HTTP 200. So a missing controller
   does not look like a 404 - it looks like a working page that never loads data.
   This script asks the database directly instead.

   SCOPE: this file changes nothing. It only SELECTs, so it is safe to run on
   production before deciding what to apply. It assumes the core CIP tables exist
   (TBL_CIP_MENU_MASTER, TBL_CIP_ROLE_MENU, TBL_CIP_CIBIL_LIMIT_REQUESTS) - on a
   database missing those, the app itself cannot work.

   Run:
     sqlcmd -S tcp:YOUR-SQL-HOST,1433 -d CIP -U sa -P **** -C -b -h -1 -W -l 30 \
            -i Backend/sql/DEPLOY-GAP-CHECK.sql
   =========================================================================== */

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

PRINT '=============================================================================';
PRINT ' 1. ERROR LOG MODULE  (Backend/sql/ERROR-LOG.sql, ERROR-LOG-MENU.sql)';
PRINT '=============================================================================';

SELECT
    '1. ERROR LOG MODULE' AS section,
    CASE WHEN OBJECT_ID(N'dbo.TBL_CIP_API_ERROR_LOG', N'U') IS NULL
         THEN '*** TABLE MISSING - run ERROR-LOG.sql. Nothing server-side is logged. ***'
         ELSE 'present' END AS table_status,
    CASE WHEN OBJECT_ID(N'dbo.TBL_CIP_API_ERROR_LOG', N'U') IS NULL
         THEN 'run Backend/sql/ERROR-LOG.sql'
         ELSE 'ok' END AS action;

SELECT
    '1. ERROR LOG MODULE' AS section,
    CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_PATH = N'/error-logs')
         THEN '*** NO MENU WITH PATH /error-logs - run ERROR-LOG-MENU.sql. The screen is unreachable. ***'
         WHEN NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER
                          WHERE MENU_PATH = N'/error-logs' AND IS_ACTIVE = 1)
         THEN '*** MENU ROW IS INACTIVE - /api/menus will never return it ***'
         ELSE 'menu row present and active' END AS menu_status,
    (SELECT TOP 1 CAST(MENU_ID AS VARCHAR(10)) + ' = ' + ISNULL(MENU_LABEL, N'?')
                  + '  path=' + ISNULL(NULLIF(MENU_PATH, N''), N'(none)')
     FROM dbo.TBL_CIP_MENU_MASTER
     WHERE MENU_ID = 2101
     ORDER BY MENU_ID) AS what_occupies_preferred_id_2101;

/* 2101 is only the ID this feature would LIKE to use. If a different screen already
   owns it, ERROR-LOG-MENU.sql allocates a free ID and touches nothing else - this
   column exists so an operator can see which screen that is. */
SELECT
    '1. ERROR LOG MODULE' AS section,
    CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_PATH = N'/error-logs')
         THEN 'no /error-logs row yet'
         WHEN (SELECT COUNT(*) FROM dbo.TBL_CIP_ROLE_MENU
                WHERE ROLE_ID IN (1, 2)
                  AND MENU_ID = (SELECT TOP 1 MENU_ID FROM dbo.TBL_CIP_MENU_MASTER
                                 WHERE MENU_PATH = N'/error-logs' ORDER BY MENU_ID)) = 2
         THEN 'granted to roles 1 and 2 - present'
         WHEN (SELECT COUNT(*) FROM dbo.TBL_CIP_ROLE_MENU
                WHERE ROLE_ID IN (1, 2)
                  AND MENU_ID = (SELECT TOP 1 MENU_ID FROM dbo.TBL_CIP_MENU_MASTER
                                 WHERE MENU_PATH = N'/error-logs' ORDER BY MENU_ID)) = 0
         THEN '*** NOT GRANTED to roles 1/2 - the menu is hidden even from Admin ***'
         ELSE '*** ONLY PARTLY GRANTED - one of role 1/2 is missing it ***'
    END AS role_grant_status;

PRINT '';
PRINT '  NOTE: the legacy table TBL_CIP_ERROR_LOG is a DIFFERENT, unrelated table that the';
PRINT '  old CIPPortal still writes to. The Error Log screen reads TBL_CIP_API_ERROR_LOG.';
PRINT '  Never point the feature at TBL_CIP_ERROR_LOG and never let a script create it.';

PRINT '';
PRINT '=============================================================================';
PRINT ' 2. CIBIL LIMIT REQUEST DOUBLE-SUBMIT GUARD';
PRINT '    (Backend/sql/LIMIT-REQUEST-DEDUPE.sql)';
PRINT '=============================================================================';

SELECT
    '2. LIMIT REQUEST DEDUPE' AS section,
    CASE WHEN OBJECT_ID(N'dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS', N'U') IS NULL
         THEN '*** TABLE MISSING - the whole limit-request feature cannot work ***'
         WHEN NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE  name = N'UX_CIBIL_LIMIT_REQ_PENDING'
                AND    object_id = OBJECT_ID(N'dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS'))
         THEN '*** INDEX MISSING - run LIMIT-REQUEST-DEDUPE.sql. The browser-side guard alone is bypassable. ***'
         ELSE 'present' END AS index_status;

PRINT '';
PRINT '  Without that index the in-app button disables itself after one click, but two';
PRINT '  tabs, a fast double-click or a replayed request can still create two Pending';
PRINT '  rows for the same employee and month. With the index, the second insert is';
PRINT '  rejected by SQL Server and the API turns it into the same friendly 400.';

PRINT '';
PRINT '  Duplicate Pending rows already present (these BLOCK the index from being';
PRINT '  created - the script reports them instead of failing):';

SELECT
    '2. LIMIT REQUEST DEDUPE' AS section,
    EMP_CODE, REQUEST_MONTH, COUNT(*) AS pending_rows
FROM   dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS
WHERE  STATUS = N'Pending'
GROUP  BY EMP_CODE, REQUEST_MONTH
HAVING COUNT(*) > 1;

PRINT '';
PRINT '=============================================================================';
PRINT ' 3. SINGLE SESSION  (Backend/sql/USER-SINGLE-SESSION.sql)';
PRINT '=============================================================================';

SELECT
    '3. SINGLE SESSION' AS section,
    CASE WHEN OBJECT_ID(N'dbo.TBL_CIP_ACTIVE_SESSION', N'U') IS NULL
         THEN '*** SESSIONS TABLE MISSING - run USER-SINGLE-SESSION.sql ***'
         ELSE 'present' END AS sessions_table,
    CASE WHEN OBJECT_ID(N'dbo.TBL_CIP_ACTIVE_SESSION', N'U') IS NOT NULL
          AND NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE  name = N'IX_CIP_ACTIVE_SESSION_JTI'
                AND    object_id = OBJECT_ID(N'dbo.TBL_CIP_ACTIVE_SESSION'))
         THEN 'JTI index missing - harmless, but every session check scans the table'
         ELSE 'ok' END AS session_index,
    N'TBL_CIP_ACTIVE_SESSION (not TBL_CIP_USER_SESSION)' AS note;

PRINT '';
PRINT '=============================================================================';
PRINT ' 4. SUMMARY - run these, in this order, on the server database';
PRINT '=============================================================================';

SELECT
    n AS step_no,
    f AS script,
    CASE
        WHEN f = N'ERROR-LOG.sql'
             AND OBJECT_ID(N'dbo.TBL_CIP_API_ERROR_LOG', N'U') IS NOT NULL
        THEN 'already applied - skip'
        WHEN f = N'ERROR-LOG-MENU.sql'
             AND EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER
                         WHERE MENU_PATH = N'/error-logs' AND IS_ACTIVE = 1)
        THEN 'already applied - skip'
        WHEN f = N'LIMIT-REQUEST-DEDUPE.sql'
             AND EXISTS (SELECT 1 FROM sys.indexes
                         WHERE name = N'UX_CIBIL_LIMIT_REQ_PENDING'
                           AND object_id = OBJECT_ID(N'dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS'))
        THEN 'already applied - skip'
        WHEN f = N'CIBIL-DEDUPE-FAILED.sql'
             AND EXISTS (SELECT 1 FROM sys.indexes
                         WHERE name = N'IX_CIP_PULL_LOG_PAN_MONTH'
                           AND object_id = OBJECT_ID(N'dbo.TBL_CIP_CIBIL_PULL_LOG'))
        THEN 'already applied - skip'
        WHEN f = N'USER-SINGLE-SESSION.sql'
             AND OBJECT_ID(N'dbo.TBL_CIP_ACTIVE_SESSION', N'U') IS NOT NULL
        THEN 'already applied - skip'
        ELSE '*** RUN IT ***'
    END AS status
FROM (VALUES (1, N'ERROR-LOG.sql'),
             (2, N'ERROR-LOG-MENU.sql'),
             (3, N'LIMIT-REQUEST-DEDUPE.sql'),
             (4, N'CIBIL-DEDUPE-FAILED.sql'),
             (5, N'USER-SINGLE-SESSION.sql')
     ) AS s(n, f)
ORDER BY s.n;