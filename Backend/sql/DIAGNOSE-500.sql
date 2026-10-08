/* ============================================================================
   DIAGNOSE-500.sql  -- read-only. Finds WHY the Applications screens return 500.

   Symptom this solves:
     GET /api/applications?page=1&pageSize=25      -> 500
     GET /api/applications/existing-loans          -> 500
   while /api/menus and /api/applications/dropdowns return 200.

   The Applications endpoints are the only ones that call the two LARGEST legacy
   procedures, and they are the only ones whose tables are NOT created by
   Database/schema.sql. If the production CIP database was created from our schema
   alone, or is a partial restore, these two procedures (or the tables inside
   them) are absent and every call raises "Invalid object name" -> 500.

   SCOPE: this file can ONLY prove PRESENCE. It cannot prove a procedure is
   callable, permitted, or correctly typed. If it reports 0 missing but the API
   still returns 500, go straight to Backend/sql/VERIFY-SIGNATURES.sql.

   This script changes nothing. It only SELECTs.

   Run:
     sqlcmd -S tcp:YOUR-SQL-HOST,1433 -d CIP -U sa -P **** -C -b -h -1 -W -l 30 \
            -i Backend/sql/DIAGNOSE-500.sql
   ============================================================================ */

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

/* ---------- 1. The two procedures the failing endpoints call ---------- */
SELECT '1. PROC      ' + p.name AS item,
       CASE WHEN OBJECT_ID('dbo.' + p.name, 'P') IS NULL
            THEN 'MISSING  <-- this is the 500'
            ELSE 'present' END AS status,
       CASE WHEN OBJECT_ID('dbo.' + p.name, 'P') IS NULL
            THEN 'Restore from a full CIP backup'
            ELSE 'ok' END AS action
FROM (VALUES ('USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING'),
             ('USP_CIP_EXISTINGLOAN_DETAILS')
     ) AS p(name)
ORDER BY status, p.name;

/* ---------- 2. Which of the 500 is which ---------- */
SELECT '2. ENDPOINT ' + e.ep AS item,
       CASE WHEN OBJECT_ID('dbo.' + e.procname, 'P') IS NULL
            THEN 'FAILS - procedure absent'
            ELSE 'procedure present; if it still 500s the cause is NOT a missing object - run VERIFY-SIGNATURES.sql'
       END AS status
FROM (VALUES ('GET /api/applications            -> USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING',
              'USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING'),
             ('GET /api/applications/existing-loans -> USP_CIP_EXISTINGLOAN_DETAILS',
              'USP_CIP_EXISTINGLOAN_DETAILS')
     ) AS e(ep, procname);

/* ---------- 3. Tables the paging procedure reads ---------- */
/* These are NOT created by Database/schema.sql. Any MISSING here breaks the search
   screen even when the procedure itself exists. */
SELECT '3. TABLE     ' + t.name AS item,
       CASE WHEN OBJECT_ID('dbo.' + t.name, 'U') IS NULL
            THEN 'MISSING  <-- this is the 500'
            ELSE 'present' END AS status,
       CASE WHEN OBJECT_ID('dbo.' + t.name, 'U') IS NULL
            THEN 'Restore from a full CIP backup'
            ELSE 'ok' END AS action
FROM (VALUES ('TBL_CIP_CLIENT_APPLICATION_DETAILS'),
             ('TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS'),
             ('TBL_CIP_USER_BANK_LOC_MAPPING'),
             ('TBL_CIP_EMPLOYEE_MASTER'),
             ('TBL_CIP_LOAN_MASTER'),
             ('TBL_CIP_LOAN_SCHEME_MASTER'),
             ('TBL_CIP_BANK_MASTER'),
             ('TBL_CIP_LOOKUP_MASTER'),
             ('TBL_CIP_APPLICATION_STATUS_MASTER'),
             ('TBL_CIP_LOGIN_CHANNEL_MASTER'),
             ('TBL_CIP_LOGIN_REGION_MASTER'),
             ('TBL_CIP_OCCUPATION_MASTER')
     ) AS t(name)
ORDER BY CASE WHEN OBJECT_ID('dbo.' + t.name, 'U') IS NULL THEN 0 ELSE 1 END, t.name;

/* ---------- 4. Row counts, to tell "table empty" from "table absent" ---------- */
/* sys.partitions rather than dm_db_partition_stats: the DMV's column names differ
   between SQL Server versions and this database has to run on the production box. */
SELECT '4. ROWCOUNT  ' + t.name AS item,
       ISNULL((SELECT CAST(SUM(p.rows) AS varchar(20)) FROM sys.partitions p
               WHERE p.object_id = OBJECT_ID('dbo.' + t.name) AND p.index_id IN (0, 1)), '0') AS status
FROM (VALUES ('TBL_CIP_CLIENT_APPLICATION_DETAILS'),
             ('TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS'),
             ('TBL_CIP_USER_BANK_LOC_MAPPING'),
             ('TBL_CIP_LOAN_MASTER'),
             ('TBL_CIP_BANK_MASTER')
     ) AS t(name)
ORDER BY t.name;

/* ---------- 5. Which tables the procedure bodies actually reference ---------- */
/* sys.sql_expression_dependencies only lists objects that RESOLVED at creation time,
   and it does NOT list user-defined TABLE TYPES (they live in sys.types, not
   sys.objects) nor SELECT-list column aliases. A short or empty list here is NOT
   evidence of a problem - do not read a missing line as a missing object. The
   authoritative check is the live EXEC in VERIFY-SIGNATURES.sql section 6. */
SELECT '5. DEPENDS ON ' + COALESCE(o.name, '(none recorded)') AS item,
       o.type_desc AS status
FROM sys.sql_modules m
LEFT JOIN sys.sql_expression_dependencies d ON d.referencing_id = m.object_id
LEFT JOIN sys.objects o ON o.object_id = d.referenced_id
WHERE m.object_id IN (OBJECT_ID('dbo.USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING'),
                      OBJECT_ID('dbo.USP_CIP_EXISTINGLOAN_DETAILS'))
ORDER BY 1;

/* ---------- 6. Summary ---------- */
SELECT '6. SUMMARY' AS item,
       (SELECT COUNT(*) FROM (VALUES ('USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING'),
                                    ('USP_CIP_EXISTINGLOAN_DETAILS')) v(n)
         WHERE OBJECT_ID('dbo.' + v.n, 'P') IS NULL) AS missing_procs,
       (SELECT COUNT(*) FROM (VALUES ('TBL_CIP_CLIENT_APPLICATION_DETAILS'),
                                    ('TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS'),
                                    ('TBL_CIP_USER_BANK_LOC_MAPPING'),
                                    ('TBL_CIP_EMPLOYEE_MASTER'),
                                    ('TBL_CIP_LOAN_MASTER'),
                                    ('TBL_CIP_LOAN_SCHEME_MASTER'),
                                    ('TBL_CIP_BANK_MASTER'),
                                    ('TBL_CIP_LOOKUP_MASTER'),
                                    ('TBL_CIP_APPLICATION_STATUS_MASTER'),
                                    ('TBL_CIP_LOGIN_CHANNEL_MASTER'),
                                    ('TBL_CIP_LOGIN_REGION_MASTER'),
                                    ('TBL_CIP_OCCUPATION_MASTER')) v(n)
         WHERE OBJECT_ID('dbo.' + v.n, 'U') IS NULL) AS missing_tables,
       CASE WHEN (SELECT COUNT(*) FROM (VALUES ('USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING'),
                                               ('USP_CIP_EXISTINGLOAN_DETAILS')) v(n)
                WHERE OBJECT_ID('dbo.' + v.n, 'P') IS NULL) > 0
             THEN 'restore the missing PROCEDURES from a full CIP backup'
             WHEN (SELECT COUNT(*) FROM (VALUES ('TBL_CIP_CLIENT_APPLICATION_DETAILS'),
                                               ('TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS'),
                                               ('TBL_CIP_USER_BANK_LOC_MAPPING'),
                                               ('TBL_CIP_EMPLOYEE_MASTER'),
                                               ('TBL_CIP_LOAN_MASTER'),
                                               ('TBL_CIP_LOAN_SCHEME_MASTER'),
                                               ('TBL_CIP_BANK_MASTER'),
                                               ('TBL_CIP_LOOKUP_MASTER'),
                                               ('TBL_CIP_APPLICATION_STATUS_MASTER'),
                                               ('TBL_CIP_LOGIN_CHANNEL_MASTER'),
                                               ('TBL_CIP_LOGIN_REGION_MASTER'),
                                               ('TBL_CIP_OCCUPATION_MASTER')) v(n)
                WHERE OBJECT_ID('dbo.' + v.n, 'U') IS NULL) > 0
             THEN 'procs present; restore the missing TABLE(S) from a full CIP backup'
             ELSE 'EVERYTHING IS PRESENT - the database is NOT the cause. Run VERIFY-SIGNATURES.sql (permissions / TVP type / signature / live EXEC).'
        END AS verdict;

/* ============================================================================
   HOW TO FIX
   Section 1/3 tell you what is absent. None of it is created by our deploy SQL -
   these are legacy CIP objects. Restore them from a full CIP database backup:

     1. On the machine holding the full CIP database, script the missing objects:
          SELECT 'CREATE OR ALTER ' + type_char + ' dbo.' + name + ' AS' + CHAR(13) + definition
        FROM sys.sql_modules JOIN sys.objects ...
        (or use SSMS "Script Table as > CREATE Table" / "Stored Procedures")

     2. Paste into sqlcmd against production and re-run this file until
        missing_procs = 0 and missing_tables = 0.

     3. Then GET /api/applications?page=1&pageSize=25 returns 200.

   IMPORTANT: TBL_CIP_CLIENT_APPLICATION_DETAILS holds real production data. If it
   is missing because the production database is incomplete, RESTORE THE DATABASE -
   do not create an empty table to make the error go away.

   IF missing_procs = 0 AND missing_tables = 0
   --------------------------------------------------------------------------
   Nothing is absent, so restoring objects will NOT fix the 500. Run
   Backend/sql/VERIFY-SIGNATURES.sql instead. It checks the things this file
   structurally cannot:

     * which SQL login the API actually connects as, and whether it has EXECUTE
       on the two procedures (HAS_PERMS_BY_NAME resolves inherited role grants).
       Running this file as sa hides exactly this class of fault.
     * the TVP dbo.TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type. A missing table type
       breaks ONLY /api/applications/existing-loans and is invisible to every
       OBJECT_ID()/sys.objects check in this file.
     * the parameter signatures the C# binds by name.
     * a live EXEC of both procedures, printing the real ERROR_NUMBER().

   This was the actual situation observed in production: 0 missing objects, and the
   500 persisted because the cause was not an absent object at all.
   ============================================================================ */