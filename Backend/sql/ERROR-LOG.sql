/* =============================================================================
   TBL_CIP_API_ERROR_LOG — the table behind the admin "Error Log" screen.

   Every unhandled exception and every caught database failure in the API is
   written here by ErrorLogService, so an admin can see what broke, on which
   endpoint, for which user, at what time — without IIS logs and without the
   person who hit the bug needing to report it.

   NAME COLLISION — READ THIS FIRST
   --------------------------------
   The database already contains a LEGACY table named dbo.TBL_CIP_ERROR_LOG.
   It is NOT ours and NOT an application error log: it is a debug trace table
   that the old CIPPortal writes to (4,000+ rows of messages like "Before
   Inserting in History"), with an unrelated schema:

       ID, ERROR_DESC, ERROR_LINE_NO, ERROR_MODULE, ERROR_DATE,
       QUERY_STRING, DB_OBJECT, RESOLUTION_NOTE, IS_RESOLVED

   Writing API exceptions into it would interleave two unrelated meanings under
   one name and break whatever reads it today. So this feature uses a distinct
   name: TBL_CIP_API_ERROR_LOG. Do not "simplify" it back to TBL_CIP_ERROR_LOG.

   Idempotent: safe to run on a server that already has the table.
   Run AFTER Database/schema.sql.
============================================================================= */

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.TBL_CIP_API_ERROR_LOG', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_API_ERROR_LOG
    (
        /* Surrogate key. bigint rather than int so the identity cannot wrap during a
           bad afternoon (a retry loop can write thousands of rows a minute). */
        ERROR_ID     bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CIP_API_ERROR_LOG PRIMARY KEY,

        /* When the error happened. datetime2(3): millisecond precision is what makes
           "which of these 40 rows is mine" answerable from the screen. */
        ERROR_ON     datetime2(3)   NOT NULL CONSTRAINT DF_CIP_API_ERROR_LOG_ERROR_ON DEFAULT (SYSUTCDATETIME()),

        /* "GET /api/applications" — the request that failed. */
        HTTP_METHOD  varchar(10)    NULL,
        REQUEST_PATH nvarchar(400)  NULL,
        QUERY_STRING nvarchar(1000) NULL,

        /* "Database" for a SQL failure, "Application" for anything else. The screen
           filters on this because the question is usually "is the database unhappy?". */
        SOURCE       varchar(50)    NOT NULL CONSTRAINT DF_CIP_API_ERROR_LOG_SOURCE DEFAULT ('Application'),

        /* Exception type name and message, e.g. "SqlException" / "Invalid object name ...". */
        EXCEPTION_TYPE nvarchar(300) NULL,
        ERROR_MESSAGE nvarchar(2000) NULL,

        /* SQL Server's native error number (208, 262, 2812, 297, 300 ...) or NULL. */
        SQL_ERROR_NUMBER int          NULL,
        SQL_ERROR_STATE   int          NULL,
        SQL_ERROR_CLASS   int          NULL,

        /* Proc/table named in the message, when one can be pulled out. Cheap to
           populate from the message text and it is the field an admin actually
           searches on, so it gets its own column rather than needing a LIKE scan. */
        DB_OBJECT     nvarchar(300)  NULL,

        /* Full stack trace. nvarchar(max): stack traces are long and truncating one
           loses exactly the frame that explains the failure. */
        STACK_TRACE   nvarchar(max)  NULL,

        /* Who hit it, when the JWT is present. NULL for anonymous requests (a failed
           login, for instance) — that is meaningful, not an oversight. */
        EMP_CODE      int            NULL,
        EMP_NAME      nvarchar(200)  NULL,
        USERNAME      nvarchar(200)  NULL,
        ROLE_ID       int            NULL,

        /* Client address, to tell one user's failure from everyone's. */
        IP_ADDRESS    varchar(64)    NULL,
        USER_AGENT    nvarchar(400)  NULL,

        /* HTTP status returned to the caller (500 for an unhandled exception). */
        STATUS_CODE   int            NULL,

        /* Admin triage. 0 = not yet looked at. */
        IS_RESOLVED   bit            NOT NULL CONSTRAINT DF_CIP_API_ERROR_LOG_IS_RESOLVED DEFAULT (0),
        RESOLVED_BY   int            NULL,
        RESOLVED_ON   datetime       NULL,
        RESOLUTION_NOTE nvarchar(1000) NULL
    );

    /* The screen's default view is unresolved-first/newest-first, so the index leads
       with IS_RESOLVED then ERROR_ON descending. */
    CREATE NONCLUSTERED INDEX IX_CIP_API_ERROR_LOG_RESOLVED_ON
        ON dbo.TBL_CIP_API_ERROR_LOG (IS_RESOLVED, ERROR_ON DESC);

    /* Supports "is this endpoint failing repeatedly". */
    CREATE NONCLUSTERED INDEX IX_CIP_API_ERROR_LOG_PATH
        ON dbo.TBL_CIP_API_ERROR_LOG (REQUEST_PATH, ERROR_ON DESC);

    /* Supports filtering to database faults, which is the most common triage filter. */
    CREATE NONCLUSTERED INDEX IX_CIP_API_ERROR_LOG_SOURCE
        ON dbo.TBL_CIP_API_ERROR_LOG (SOURCE, ERROR_ON DESC);

    PRINT 'TBL_CIP_API_ERROR_LOG created.';
END
ELSE
    PRINT 'TBL_CIP_API_ERROR_LOG already exists - leaving as is.';
GO

/* Additive column migrations: a server deployed from an older build of this script
   keeps working, and only genuinely new columns are added. */
IF COL_LENGTH('dbo.TBL_CIP_API_ERROR_LOG', 'QUERY_STRING') IS NULL
    ALTER TABLE dbo.TBL_CIP_API_ERROR_LOG ADD QUERY_STRING nvarchar(1000) NULL;
GO
IF COL_LENGTH('dbo.TBL_CIP_API_ERROR_LOG', 'DB_OBJECT') IS NULL
    ALTER TABLE dbo.TBL_CIP_API_ERROR_LOG ADD DB_OBJECT nvarchar(300) NULL;
GO
IF COL_LENGTH('dbo.TBL_CIP_API_ERROR_LOG', 'RESOLUTION_NOTE') IS NULL
    ALTER TABLE dbo.TBL_CIP_API_ERROR_LOG ADD RESOLUTION_NOTE nvarchar(1000) NULL;
GO
IF COL_LENGTH('dbo.TBL_CIP_API_ERROR_LOG', 'SOURCE') IS NULL
    ALTER TABLE dbo.TBL_CIP_API_ERROR_LOG ADD SOURCE varchar(50) NOT NULL
        CONSTRAINT DF_CIP_API_ERROR_LOG_SOURCE_MIG DEFAULT ('Application');
GO

PRINT 'TBL_CIP_API_ERROR_LOG ready.';

/* Guard against the legacy collision coming back: if someone renames this table to
   TBL_CIP_ERROR_LOG, the legacy table is already there and the CREATE is skipped,
   which would silently produce a screen reading the wrong table. */
IF OBJECT_ID(N'dbo.TBL_CIP_ERROR_LOG', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.TBL_CIP_API_ERROR_LOG', N'U') IS NOT NULL
    PRINT 'NOTE: legacy dbo.TBL_CIP_ERROR_LOG also exists. This feature reads/writes TBL_CIP_API_ERROR_LOG and must not be pointed at the legacy table.';

/* Verification */
SELECT c.name AS column_name, ty.name AS data_type, c.max_length, c.is_nullable
  FROM sys.columns c
  JOIN sys.types ty ON ty.user_type_id = c.user_type_id
 WHERE c.object_id = OBJECT_ID(N'dbo.TBL_CIP_API_ERROR_LOG')
 ORDER BY c.column_id;

SELECT i.name AS index_name, i.type_desc
  FROM sys.indexes i
 WHERE i.object_id = OBJECT_ID(N'dbo.TBL_CIP_API_ERROR_LOG') AND i.name IS NOT NULL;