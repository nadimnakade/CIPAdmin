/* =============================================================================
   VERIFY-SIGNATURES.sql
   -----------------------------------------------------------------------------
   Use this when DIAGNOSE-500.sql says "0 missing" but the API still returns 500.

   DIAGNOSE-500.sql proves the objects EXIST. That is necessary but not
   sufficient. A stored procedure can be present and still uncallable, and the
   failures look identical from the browser (a 500 with no body):

     * the app's SQL login has no EXECUTE grant on the procedure, or no
       SELECT on one of the tables it reads  -> "permission denied"
     * the procedure is present in THIS database but its body references another
       database or server, which is missing or unreachable
     * the user-defined TABLE TYPE (TVP) is missing. Types live in sys.types, not
       sys.objects, so NO object-based diagnostic can ever report this - and it
       breaks only the existing-loans endpoint, which matches the symptom exactly
     * a parameter the app binds is absent from the signature

   Read-only in the sense that it changes no schema and no data. It DOES execute
   two SELECT-only procedures, and it reports which SQL login you are connected
   as, because that is very often the whole answer.

   NOTE ON RUNNING AS THE WRONG USER: running this in SSMS as sa or as
   Administrator proves nothing about permissions. Section 2 tells you the login
   to re-run this as.
   ========================================================================== */

SET NOCOUNT ON;

PRINT '=== 1. Who am I? (run section 2 as the APP login, not as sa) ===';
SELECT
    ORIGINAL_LOGIN()          AS original_login,
    SUSER_SNAME()             AS effective_login,
    DB_NAME()                 AS database_name,
    @@SERVERNAME              AS server_name,
    'If this is NOT the login in the API appsettings connection string, every check below is inconclusive.' AS warning;

PRINT '';
PRINT '=== 2. Can the app login actually call the two procedures? ===';
-- Replace @app_login with the User ID from the deployed appsettings.json
-- ConnectionStrings:Default. Keeping it a variable makes the answer explicit.
DECLARE @app_login sysname = N'sa';   -- <-- CHANGE THIS to the app's SQL login

-- HAS_PERMS_BY_NAME is used deliberately instead of joining sys.database_permissions:
-- that view keys on major_id, not object_id, and a hand-rolled join is easy to get
-- subtly wrong in a way that silently reports "not granted" for a grant that exists.
-- HAS_PERMS_BY_NAME also resolves INHERITED grants through roles, which is what
-- actually decides whether the API can run.
SELECT
    @app_login AS app_login,
    CASE WHEN EXISTS (SELECT 1 FROM sys.server_principals
                      WHERE name = @app_login AND type IN ('S','U'))
         THEN 'login exists' ELSE 'LOGIN DOES NOT EXIST - fix the connection string first' END AS login_status,
    HAS_PERMS_BY_NAME(N'dbo', N'USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING', N'EXECUTE')  AS paging_proc_execute,
    CASE WHEN HAS_PERMS_BY_NAME(N'dbo', N'USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING', N'EXECUTE') = 0
         THEN 'NO EXECUTE GRANT - guaranteed 500 for /api/applications'
         ELSE 'granted' END AS paging_proc_verdict,
    HAS_PERMS_BY_NAME(N'dbo', N'USP_CIP_EXISTINGLOAN_DETAILS', N'EXECUTE')            AS existingloan_proc_execute,
    CASE WHEN HAS_PERMS_BY_NAME(N'dbo', N'USP_CIP_EXISTINGLOAN_DETAILS', N'EXECUTE') = 0
         THEN 'NO EXECUTE GRANT - guaranteed 500 for /existing-loans'
         ELSE 'granted' END AS existingloan_verdict,
    HAS_PERMS_BY_NAME(N'dbo', N'TBL_CIP_CLIENT_APPLICATION_DETAILS', N'SELECT')      AS appdetails_select,
    HAS_PERMS_BY_NAME(N'dbo', N'TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS', N'SELECT')     AS existingloan_select;

PRINT '';
PRINT '=== 3. The TVP (invisible to any sys.objects-based diagnostic) ===';
SELECT
    CASE WHEN EXISTS (SELECT 1 FROM sys.types t
                      JOIN sys.schemas s ON s.schema_id = t.schema_id
                      WHERE t.name = N'TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type'
                        AND s.name = N'dbo' AND t.is_table_type = 1)
         THEN 'present' ELSE 'MISSING' END AS tvp_type,
    CASE WHEN EXISTS (SELECT 1 FROM sys.types t
                      JOIN sys.schemas s ON s.schema_id = t.schema_id
                      WHERE t.name = N'TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type'
                        AND s.name = N'dbo' AND t.is_table_type = 1)
         THEN 'ok' ELSE 'RESTORE THE TYPE. This alone explains the existing-loans 500.' END AS action;

PRINT '  -- TVP columns the app supplies --';
SELECT c.column_id, c.name AS tvp_column, t.name AS type_name, c.max_length
FROM sys.columns c
JOIN sys.types   t ON t.user_type_id = c.user_type_id
JOIN sys.objects o ON o.object_id   = c.object_id
WHERE o.name = N'TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type' AND o.type IN ('TT','TP')
ORDER BY c.column_id;

PRINT '';
PRINT '=== 4. Do the procedure signatures still accept what the app binds? ===';
PRINT '  the app binds these 12 names by name to /api/applications';
;WITH expected(param_name) AS (
    SELECT * FROM (VALUES
        ('@DisplayLength'),('@DisplayStart'),('@SortCol'),('@SortDir'),
        ('@SearchText'),('@UserKey'),('@FLoginRegion'),('@FBank'),
        ('@FCaller'),('@FSupervisor'),('@FStatus'),('@FAppDate')
    ) v(param_name)
)
SELECT e.param_name AS app_binds,
       ISNULL(p.name, '(absent)') AS db_has,
       ISNULL(t.name, '') AS db_type,
       CASE WHEN p.name IS NULL
            THEN 'PARAMETER MISSING - guaranteed 500'
            ELSE 'present' END AS verdict
FROM expected e
LEFT JOIN sys.objects   o ON o.name = N'USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING'
LEFT JOIN sys.parameters p ON p.object_id = o.object_id AND LOWER(p.name) = LOWER(e.param_name)
LEFT JOIN sys.types     t ON t.user_type_id = p.user_type_id;

PRINT '  -- parameters the DB has that the app never sends (a proc without defaults) --';
SELECT p.name AS extra_param, t.name AS type_name
FROM sys.parameters p
JOIN sys.objects o ON o.object_id = p.object_id
JOIN sys.types   t ON t.user_type_id = p.user_type_id
WHERE o.name = N'USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING'
  AND LOWER(p.name) NOT IN ('@displaylength','@displaystart','@sortcol','@sortdir',
                            '@searchtext','@userkey','@floginregion','@fbank','@fcaller',
                            '@fsupervisor','@fstatus','@fappdate');

PRINT '  -- existing-loans signature --';
SELECT p.parameter_id, p.name AS param_name, t.name AS type_name
FROM sys.parameters p
JOIN sys.objects o ON o.object_id = p.object_id
JOIN sys.types   t ON t.user_type_id = p.user_type_id
WHERE o.name = N'USP_CIP_EXISTINGLOAN_DETAILS'
ORDER BY p.parameter_id;

PRINT '';
PRINT '=== 5. Cross-database / cross-server references inside the procedure bodies ===';
PRINT '  a proc present here that reads another database fails only at runtime';
SELECT d.referenced_schema_name AS ref_schema,
       d.referenced_entity_name  AS ref_entity,
       CASE WHEN d.referenced_server_name IS NULL THEN 'local' ELSE 'LINKED SERVER' END AS location,
       CASE WHEN d.referenced_database_name IS NULL THEN '(same db)'
            ELSE d.referenced_database_name END AS ref_database
FROM sys.sql_expression_dependencies d
WHERE d.referencing_id IN (
        SELECT object_id FROM sys.objects WHERE name = N'USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING'
    UNION ALL
        SELECT object_id FROM sys.objects WHERE name = N'USP_CIP_EXISTINGLOAN_DETAILS')
  AND (d.referenced_database_name IS NOT NULL OR d.referenced_server_name IS NOT NULL);
-- no rows above = fully self-contained, which is what you want

PRINT '';
PRINT '=== 6. LIVE EXECUTION TEST - the authoritative check ===';
PRINT '  This calls the procedures exactly as the API does and prints the real';
PRINT '  SQL error number and message instead of guessing. Change @emp to the';
PRINT '  empCode of a real signed-in user (Admin is 10).';

DECLARE @emp int = 10;
DECLARE @empkey varchar(50) = CAST(10 AS varchar(50));

BEGIN TRY
    PRINT '  -- 6a. USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING (page 1, 26 rows requested) --';
    -- Deliberately NOT "INSERT ... EXEC": binding the proc output into a table
    -- variable would impose MY guessed column types, so a harmless type widening
    -- could masquerade as a failure. A bare EXEC runs the proc exactly as the API
    -- does and returns its rows to the client.
    EXEC dbo.USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING
        @DisplayLength=26, @DisplayStart=0, @SortCol=1, @SortDir=N'asc',
        @SearchText=N'', @UserKey=@emp,
        @FLoginRegion=N'', @FBank=N'', @FCaller=N'',
        @FSupervisor=N'', @FStatus=N'', @FAppDate=N'';

    PRINT '     SUCCEEDED - the rows above are what /api/applications returns (app trims to 25).';
END TRY
BEGIN CATCH
    PRINT '     FAILED - this is the real cause of the 500:';
    SELECT ERROR_NUMBER()    AS sql_error_number,
           ERROR_SEVERITY()  AS severity,
           ERROR_STATE()     AS state,
           ERROR_MESSAGE()   AS error_message,
           ERROR_LINE()      AS error_line;
    PRINT '     Run the same EXEC in the proc''s own context to see which inner statement fails.';
END CATCH

PRINT '';
IF EXISTS (SELECT 1 FROM sys.types t JOIN sys.schemas s ON s.schema_id = t.schema_id
           WHERE t.name = N'TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type'
             AND s.name = N'dbo' AND t.is_table_type = 1)
BEGIN
    BEGIN TRY
        PRINT '  -- 6b. USP_CIP_EXISTINGLOAN_DETAILS (empty TVP, exactly as the API sends it) --';
        -- Dynamic SQL on purpose: DECLARE @tvp dbo.<type> is resolved when the batch
        -- COMPILES, so if the type were missing this batch would fail to compile and
        -- take sections 1-5 down with it. sp_executesql defers that to run time.
        EXEC sp_executesql N'
            DECLARE @tvp dbo.TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type;
            DECLARE @by varchar(50) = @empkey;
            EXEC dbo.USP_CIP_EXISTINGLOAN_DETAILS @Action=''F'',
                 @CREATED_BY=@by, @TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type=@tvp;',
            N'@empkey varchar(50)', @empkey = @empkey;
        PRINT '     SUCCEEDED - the rows above are what /existing-loans returns.';
    END TRY
    BEGIN CATCH
        PRINT '     FAILED - this is the real cause of the 500:';
        SELECT ERROR_NUMBER()    AS sql_error_number,
               ERROR_SEVERITY()  AS severity,
               ERROR_STATE()     AS state,
               ERROR_MESSAGE()   AS error_message,
               ERROR_LINE()      AS error_line;
    END CATCH;
END;
ELSE
    PRINT '  -- 6b SKIPPED: the TVP type is missing, so this call cannot even compile. Restore it. --';

PRINT '';
PRINT '=== 7. SUMMARY ===';
SELECT
    CASE WHEN EXISTS (SELECT 1 FROM sys.types t JOIN sys.schemas s ON s.schema_id = t.schema_id
                      WHERE t.name = N'TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type'
                        AND s.name = N'dbo' AND t.is_table_type = 1)
         THEN 'present' ELSE 'MISSING' END AS tvp_type,
    (SELECT COUNT(*) FROM sys.parameters p JOIN sys.objects o ON o.object_id = p.object_id
     WHERE o.name = N'USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING') AS paging_params,
    (SELECT COUNT(*) FROM sys.parameters p JOIN sys.objects o ON o.object_id = p.object_id
     WHERE o.name = N'USP_CIP_EXISTINGLOAN_DETAILS') AS existingloan_params,
    CASE
        WHEN SUSER_SNAME() <> @app_login
            THEN 'You are NOT connected as the app login - sections 2 and 6 are inconclusive. Re-run as ' + @app_login + '.'
        ELSE 'section 6 results above are authoritative'
    END AS how_to_read_this;
PRINT 'If 6a/6b both SUCCEEDED here but the API still 500s, the problem is not the';
PRINT 'database: deploy the current build so the response carries a JSON error body';
PRINT 'naming the real exception, and check the IIS/stdout log.';