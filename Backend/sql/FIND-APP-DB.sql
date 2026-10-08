/* =============================================================================
   FIND-APP-DB.sql  -- find why the API 500s when SSMS works fine.

   SITUATION THIS SOLVES
   ---------------------
   You ran VERIFY-SIGNATURES.sql in SSMS and the procedures succeeded and returned
   data - yet GET /api/applications, /api/applications/{id} and
   /api/applications/existing-loans all return 500 from the API. The database is
   provably healthy *for your SSMS session*. So the difference is the CONTEXT the
   API connects with. There are only three, and this script tests all three:

     A. THE APP POINTS AT A DIFFERENT DATABASE OR SERVER.
        The deployed appsettings.json may name another DB (or another instance) where
        these procs genuinely do not exist. Everything in SSMS is then true AND the
        500 is real. Section 1 answers this.

     B. THE APP'S SQL LOGIN LACKS PERMISSIONS.
        These procs are owned by someone else and the app connects as a low-privilege
        login. EXECUTE is denied -> 500, while your Administrator session works.
        Section 2 answers this, and it uses EXECUTE AS USER so you do NOT have to
        reconnect SSMS as that login. It covers all three callers:
          GET /api/applications             -> USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING
          GET /api/applications/{id}        -> USP_CIP_GET_CLIENT_DETAILS
          GET /api/applications/existing-loans -> USP_CIP_EXISTINGLOAN_DETAILS

     C. THE CALL EXCEEDS THE APP'S COMMAND TIMEOUT.
        DbContext sets CommandTimeout = 60s for normal queries (180s for
        existing-loans). SSMS has NO timeout by default, so a slow proc succeeds in
        SSMS and dies in the app. Section 4 measures it.

   WHY NOT JUST CONNECT SSMS AS THE APP LOGIN?
   Because EXECUTE AS USER reproduces the database permissions from inside the
   session you already have open, and it cannot be forgotten or fat-fingered.

   Read-only. Selects and two EXECs only; creates nothing.
============================================================================= */

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @AppLogin   sysname = N'REPLACE_ME';   -- User ID from the deployed appsettings.json
DECLARE @AppDatabase sysname = N'CIP';         -- Initial Catalog from the same connection string
DECLARE @AppServer   nvarchar(200) = N'REPLACE_ME'; -- the Data Source / Server value

/* THE AUTHORITATIVE FLAGS for section B. Declared here, at batch scope, because the
   VERDICT at the end reads them even when the login does not exist and section B is
   skipped entirely - declaring them inside that branch would make the whole script
   fail to compile in exactly the case where it is most needed. */
DECLARE @pagingExecOk bit = 0, @loansExecOk bit = 0, @detailsExecOk bit = 0;

PRINT '=============================================================================';
PRINT ' A. WHICH DATABASE IS THE API ACTUALLY TALKING TO?';
PRINT '=============================================================================';

SELECT
    ORIGINAL_LOGIN() AS your_ssms_login,
    @@SERVERNAME     AS server_this_script_ran_on,
    DB_NAME()        AS database_this_script_ran_on,
    @AppServer      AS server_from_appsettings,
    @AppDatabase     AS database_from_appsettings,
    CASE
        WHEN @AppLogin = N'REPLACE_ME'
            THEN 'FIX THE VARIABLES AT THE TOP FIRST - @AppLogin is still the placeholder'
        WHEN @AppDatabase <> DB_NAME()
            THEN '*** MISMATCH: you are probing ' + QUOTENAME(DB_NAME())
                 + ' but the API is configured for ' + QUOTENAME(@AppDatabase)
                 + '. Re-run this script against the DATABASE the API actually uses. ***'
        WHEN @AppServer <> @@SERVERNAME
            THEN '*** MISMATCH: you are on ' + @@SERVERNAME + ' but the API targets ' + @AppServer + ' ***'
        ELSE 'server and database match - the API and this session see the same data'
    END AS verdict;

-- Does the configured database even exist on this server?
IF DB_ID(@AppDatabase) IS NULL
    PRINT '*** FATAL: database ' + @AppDatabase + ' does not exist on this server. ***'
ELSE
    PRINT 'database ' + @AppDatabase + ' exists on this server.';

PRINT '';
PRINT '=============================================================================';
PRINT ' B. CAN THE APP''S SQL LOGIN ACTUALLY CALL THE PROCEDURES?';
PRINT '    (EXECUTE AS USER - no need to reconnect SSMS)';
PRINT '=============================================================================';

DECLARE @LoginExists bit = CASE WHEN EXISTS (
        SELECT 1 FROM sys.server_principals WHERE name = @AppLogin AND type IN ('S','U')) THEN 1 ELSE 0 END;

SELECT
    @AppLogin AS app_login,
    CASE WHEN @LoginExists = 1
         THEN 'login exists on this server'
         ELSE '*** LOGIN DOES NOT EXIST ON THIS SERVER - the connection string points here but the login is absent, so the API cannot connect at all ***'
    END AS login_status;

IF @LoginExists = 0
BEGIN
    PRINT 'Cannot continue to the permission test: the login is not on this server.';
    PRINT 'That alone would make the API fail. Check the Data Source in appsettings.json.';
END
ELSE
BEGIN
    /* A database user must exist for EXECUTE AS USER to work. If the app connects,
       one does. If it does not, that is itself the finding. */
    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @AppLogin)
    BEGIN
        PRINT '*** The login ' + @AppLogin + ' has NO DATABASE USER in [' + DB_NAME() + ']. ***';
        PRINT 'A login without a database user cannot read any object. This alone causes the 500.';
        PRINT 'Fix (run against [' + DB_NAME() + '] as an admin):';
        PRINT '    CREATE USER ' + QUOTENAME(@AppLogin) + ' FOR LOGIN ' + QUOTENAME(@AppLogin) + ';';
    END

    /* HAS_PERMS_BY_NAME is reported for reference ONLY, and it is NOT trusted.
       Under EXECUTE AS USER it returns NULL even when the grant genuinely exists and
       the procedure runs fine - verified on this server: after
       GRANT EXECUTE ... TO <login>, it still returned NULL while the live EXEC
       succeeded. Treating that NULL as "denied" would send an operator chasing a
       permission problem they had already fixed. The live EXEC below is the truth. */
    DECLARE @pagingPerm int, @loansPerm int, @appDetailsSelect int, @empSelect int;
    DECLARE @who nvarchar(128);

    EXECUTE AS USER = @AppLogin;
        SELECT @who = USER_NAME();
        SELECT @pagingPerm      = HAS_PERMS_BY_NAME(N'dbo', N'USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING', N'EXECUTE');
        SELECT @loansPerm       = HAS_PERMS_BY_NAME(N'dbo', N'USP_CIP_EXISTINGLOAN_DETAILS', N'EXECUTE');
        SELECT @appDetailsSelect= HAS_PERMS_BY_NAME(N'dbo', N'TBL_CIP_CLIENT_APPLICATION_DETAILS', N'SELECT');
        SELECT @empSelect       = HAS_PERMS_BY_NAME(N'dbo', N'TBL_CIP_EMPLOYEE_MASTER', N'SELECT');
    REVERT;

    /* HAS_PERMS_BY_NAME returns NULL - not 0 - when the permission cannot be
       determined, which is what happens for a user with no rights on the object at
       all. Every comparison below therefore goes through ISNULL(...,0): testing
       "@perm = 0" against a NULL yields UNKNOWN, the CASE falls through, and the
       script would report "the database is fine" while the real answer is DENIED.
       That is the one mistake this script must not make, so NULL is folded to 0
       at the point of capture instead of being trusted downstream. */
    SELECT
        @who AS permissions_checked_as,
        ISNULL(@pagingPerm, 0) AS perms_by_name_EXECUTE_UNRELIABLE,
        CASE WHEN ISNULL(@pagingPerm,0) = 1 THEN 'metadata says granted' ELSE 'metadata says denied/NULL - IGNORE THIS, see the live EXEC result below' END AS paging_metadata_note,
        ISNULL(@loansPerm, 0) AS perms_by_name_EXECUTE_loans_UNRELIABLE,
        CASE WHEN ISNULL(@loansPerm,0) = 1 THEN 'metadata says granted' ELSE 'metadata says denied/NULL - IGNORE THIS, see the live EXEC result below' END AS loans_metadata_note,
        ISNULL(@appDetailsSelect,0) AS app_details_select_0_or_1,
        ISNULL(@empSelect,0) AS employee_master_select_0_or_1,
        CASE WHEN ISNULL(@empSelect,0) = 1 THEN 'granted'
             ELSE 'metadata says denied/NULL - UNRELIABLE, see the live EXEC below' END AS employee_verdict_UNRELIABLE;

    SET @pagingPerm = ISNULL(@pagingPerm, 0);
    SET @loansPerm  = ISNULL(@loansPerm, 0);

    /* Now actually try it as that user. Permission metadata says yes; a real EXEC is
       the only thing that catches a denial raised from inside the procedure body. */
    PRINT '';
    PRINT '  -- live EXEC as the app login: /api/applications --';
    BEGIN TRY
        EXECUTE AS USER = @AppLogin;
        BEGIN TRY
            EXEC dbo.USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING
                @DisplayLength=26, @DisplayStart=0, @SortCol=1, @SortDir=N'asc',
                @SearchText=N'', @UserKey=10,
                @FLoginRegion=N'', @FBank=N'', @FCaller=N'',
                @FSupervisor=N'', @FStatus=N'', @FAppDate=N'';
            PRINT '     SUCCEEDED as the app login.';
            SET @pagingExecOk = 1;
        END TRY
        BEGIN CATCH
            PRINT '     FAILED as the app login - this is the real 500:';
            SELECT ERROR_NUMBER() AS sql_error_number, ERROR_MESSAGE() AS error_message;
        END CATCH;
        REVERT;
    END TRY
    BEGIN CATCH
        PRINT '     could not impersonate the login:';
        SELECT ERROR_NUMBER() AS sql_error_number, ERROR_MESSAGE() AS error_message;
    END CATCH;

    PRINT '  -- live EXEC as the app login: /api/applications/{id} (Edit Application) --';
    BEGIN TRY
        EXECUTE AS USER = @AppLogin;
        BEGIN TRY
            EXEC dbo.USP_CIP_GET_CLIENT_DETAILS @Action='FE', @SearchText=N'1', @UserKey=10;
            PRINT '     SUCCEEDED as the app login.';
            SET @detailsExecOk = 1;
        END TRY
        BEGIN CATCH
            PRINT '     FAILED as the app login - this is the real 500:';
            SELECT ERROR_NUMBER() AS sql_error_number, ERROR_MESSAGE() AS error_message;
        END CATCH;
        REVERT;
    END TRY
    BEGIN CATCH
        PRINT '     could not impersonate the login:';
        SELECT ERROR_NUMBER() AS sql_error_number, ERROR_MESSAGE() AS error_message;
    END CATCH;

    PRINT '  -- live EXEC as the app login: /existing-loans --';
    BEGIN TRY
        EXECUTE AS USER = @AppLogin;
        BEGIN TRY
            DECLARE @tvp dbo.TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type;
            EXEC dbo.USP_CIP_EXISTINGLOAN_DETAILS @Action='F',
                 @CREATED_BY='10', @TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type=@tvp;
            PRINT '     SUCCEEDED as the app login.';
            SET @loansExecOk = 1;
        END TRY
        BEGIN CATCH
            PRINT '     FAILED as the app login - this is the real 500:';
            SELECT ERROR_NUMBER() AS sql_error_number, ERROR_MESSAGE() AS error_message;
        END CATCH;
        REVERT;
    END TRY
    BEGIN CATCH
        PRINT '     could not impersonate the login (TVP may also be missing):';
        SELECT ERROR_NUMBER() AS sql_error_number, ERROR_MESSAGE() AS error_message;
    END CATCH;

    /* The single line that matters: it is based on whether the procedure ACTUALLY RAN
       as the app login, which is the only method proven reliable here. */
    SELECT
        @AppLogin AS app_login,
        @pagingExecOk AS paging_proc_actually_ran,
        CASE WHEN @pagingExecOk = 1 THEN 'OK - /api/applications would NOT 500 on permissions'
             ELSE '*** BLOCKED - this is why /api/applications 500s ***' END AS paging_truth,
        @loansExecOk AS existingloans_actually_ran,
        CASE WHEN @loansExecOk = 1 THEN 'OK - /existing-loans would NOT 500 on permissions'
             ELSE '*** BLOCKED - this is why /existing-loans 500s ***' END AS loans_truth,
        @detailsExecOk AS appdetails_actually_ran,
        CASE WHEN @detailsExecOk = 1 THEN 'OK - /applications/{id} would NOT 500 on permissions'
             ELSE '*** BLOCKED - this is why /applications/{id} 500s ***' END AS appdetails_truth;

    /* THE GRANT PEOPLE FORGET.
       A TVP parameter needs its own permission on the TABLE TYPE, not just on the
       procedure. Verified on this server: with EXECUTE granted on the procedure AND on
       every table, /existing-loans still failed with

           229  The EXECUTE permission was denied on the object
                'TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type'

       until this grant was added. It matches the production symptom exactly - it
       breaks ONLY /existing-loans, and it is invisible to every existence check
       because the type is present, it is just not granted. If existing-loans came
       back BLOCKED above, run this. */
    IF @loansExecOk = 0
    BEGIN
        PRINT '';
        PRINT ' Most likely cause for /existing-loans: the TVP type is not granted.';
        PRINT ' Fix (run as an admin, in this database):';
        PRINT '     GRANT EXECUTE ON TYPE::dbo.TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type TO '
              + QUOTENAME(@AppLogin) + ';';
        PRINT ' If /api/applications is the one blocked, grant instead:';
        PRINT '     GRANT EXECUTE ON dbo.USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING TO '
              + QUOTENAME(@AppLogin) + ';';
        PRINT '     GRANT EXECUTE ON dbo.USP_CIP_GET_CLIENT_DETAILS TO '
              + QUOTENAME(@AppLogin) + ';';
        PRINT '     GRANT SELECT ON the tables it reads TO ' + QUOTENAME(@AppLogin) + ';';
    END
END

PRINT '';
PRINT '=============================================================================';
PRINT ' C. DOES THE CALL EXCEED THE APP''S COMMAND TIMEOUT?';
PRINT '    App uses CommandTimeout 60s (180s for existing-loans). SSMS has none.';
PRINT '=============================================================================';

DECLARE @t datetime2 = SYSUTCDATETIME();
BEGIN TRY
    EXEC dbo.USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING
        @DisplayLength=26, @DisplayStart=0, @SortCol=1, @SortDir=N'asc',
        @SearchText=N'', @UserKey=10,
        @FLoginRegion=N'', @FBank=N'', @FCaller=N'',
        @FSupervisor=N'', @FStatus=N'', @FAppDate=N'';
END TRY
BEGIN CATCH
    PRINT '  the paging procedure did not even run as you:';
    SELECT ERROR_NUMBER() AS sql_error_number, ERROR_MESSAGE() AS error_message;
END CATCH;

DECLARE @pagingMs int = DATEDIFF(ms, @t, SYSUTCDATETIME());

DECLARE @t2 datetime2 = SYSUTCDATETIME();
BEGIN TRY
    DECLARE @tvp2 dbo.TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type;
    EXEC dbo.USP_CIP_EXISTINGLOAN_DETAILS @Action='F',
         @CREATED_BY='10', @TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type=@tvp2;
END TRY
BEGIN CATCH
    PRINT '  the existing-loans procedure did not even run as you:';
    SELECT ERROR_NUMBER() AS sql_error_number, ERROR_MESSAGE() AS error_message;
END CATCH;
DECLARE @loansMs int = DATEDIFF(ms, @t2, SYSUTCDATETIME());

SELECT
    @pagingMs AS paging_ms,
    60000 AS app_paging_timeout_ms,
    CASE WHEN @pagingMs < 20000 THEN 'comfortable here - but a loaded production box can be several times slower'
         WHEN @pagingMs < 60000 THEN 'under the timeout but not by much; a loaded server WILL exceed it'
         ELSE '*** ALREADY OVER THE 60s APP TIMEOUT - this alone explains /api/applications ***'
    END AS paging_risk,
    @loansMs AS existingloans_ms,
    180000 AS app_existingloans_timeout_ms,
    CASE WHEN @loansMs < 180000 THEN 'fine - existing-loans is too cheap to be a timeout problem'
         ELSE '*** over the 180s timeout ***' END AS loans_risk;

PRINT 'Measure a DEEP page too - the procedure pages with OFFSET-style RowNum filtering,';
PRINT 'so late pages are slower: re-run section C with @DisplayStart = 80000.';

PRINT '';
PRINT '=============================================================================';
PRINT ' VERDICT';
PRINT '=============================================================================';
SELECT
    CASE
        WHEN @AppLogin = N'REPLACE_ME'
            THEN 'Edit @AppLogin (and @AppServer/@AppDatabase) at the top of this file, then re-run.'
        WHEN DB_ID(@AppDatabase) IS NULL
            THEN 'THE API IS CONFIGURED FOR A DATABASE THAT DOES NOT EXIST HERE. Fix the connection string.'
        WHEN @LoginExists = 0
            THEN 'THE API''S SQL LOGIN DOES NOT EXIST ON THIS SERVER. Fix the connection string.'
        WHEN ISNULL(@pagingExecOk,0) = 0 OR ISNULL(@loansExecOk,0) = 0
            THEN 'PERMISSIONS. The app login cannot EXECUTE the procedures (live EXEC failed above). Grant it.'
        WHEN @pagingMs >= 60000
            THEN 'TIMEOUT. The procedure is slower than the app''s 60s CommandTimeout. Raise the timeout.'
        ELSE 'None of A/B/C tripped. The database is fine for this login. Deploy the current build so the 500 carries a JSON error body naming the exception - and read TBL_CIP_API_ERROR_LOG on the Error Log screen.'
    END AS verdict;