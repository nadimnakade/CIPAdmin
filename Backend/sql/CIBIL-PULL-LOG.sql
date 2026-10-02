/* ============================================================================
   CIBIL pull ledger — the counting source of truth for CIBIL usage.

   Why this exists
   ---------------
   TBL_CIP_CIBIL_REPORTS holds one row per REPORT ARTEFACT (one PAN per month),
   not one row per PULL. Counting that table for a user's monthly usage is wrong
   in two ways:
     * a cached pull (same PAN, same month) never inserts a row, so it is invisible;
     * counting is attributed to whoever first pulled the PAN, not to whoever
       requested the report.
   That is the "0 / 10" bug: a TL whose PANs were all pulled by colleagues shows 0.

   This table records one row per pull EVENT. All counts on the Admin and TL tabs
   are derived from it. See docs/CIBIL-REPORTS-SPEC.md.

   SQL Server 2014 compatible. Idempotent — safe to run more than once.
   ============================================================================ */

SET NOCOUNT ON;
GO

/* ---------------------------------------------------------------- 1. ledger */
IF OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_CIBIL_PULL_LOG (
        LOG_ID        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CIP_CIBIL_PULL_LOG PRIMARY KEY,
        REPORT_ID     INT            NULL,          -- report served; NULL when the vendor call failed
        PAN_NO        VARCHAR(10)    NOT NULL,
        REPORT_TYPE   VARCHAR(20)    NOT NULL CONSTRAINT DF_CIP_PULL_TYPE DEFAULT ('cibil'),
        EMP_CODE      INT            NOT NULL,     -- the REQUESTER, not the original puller
        EMP_NAME      VARCHAR(200)   NOT NULL,
        LOCATION_ID   INT            NULL,         -- snapshot of the requester's location
        LOCATION_NAME VARCHAR(500)   NULL,         -- width matches TBL_CIP_LOCATION_MASTER.LOC_NAME
        PULLED_BY_NAME VARCHAR(100)  NULL,         -- who the artefact is credited to
        -- API = fresh vendor pull | CACHE = same-month reuse | LEGACY = pre-migration PDF
        -- REGEN = admin forced refresh | BACKFILL = seeded from a pre-ledger report row
        SOURCE        VARCHAR(10)    NOT NULL,
        STATUS        VARCHAR(10)    NOT NULL CONSTRAINT DF_CIP_PULL_STATUS DEFAULT ('OK'),  -- OK | FAILED
        PULLED_DATE   DATETIME       NOT NULL CONSTRAINT DF_CIP_PULL_DATE DEFAULT (GETDATE()),
        IP_ADDRESS    VARCHAR(45)    NULL,
        USER_AGENT    VARCHAR(400)   NULL
    );

    PRINT 'TBL_CIP_CIBIL_PULL_LOG created.';
END
ELSE
    PRINT 'TBL_CIP_CIBIL_PULL_LOG already exists — skipped.';
GO

/* PULL_MONTH is a computed column so the quota window key can never disagree
   with PULLED_DATE. Needs QUOTED_IDENTIFIER ON to add a computed column. */
SET QUOTED_IDENTIFIER ON;
GO
IF OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.computed_columns
                   WHERE object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG') AND name = 'PULL_MONTH')
    EXEC('ALTER TABLE dbo.TBL_CIP_CIBIL_PULL_LOG
          ADD PULL_MONTH AS CONVERT(VARCHAR(7), PULLED_DATE, 126) PERSISTED');
GO

IF OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG', 'U') IS NOT NULL
BEGIN
    -- STATUS must be wide enough for 'FAILED' (6 chars). An earlier revision declared
    -- varchar(3), which silently rejected failed-pull rows; widen any existing column.
    IF EXISTS (SELECT 1 FROM sys.types t
               JOIN sys.columns c ON c.user_type_id = t.user_type_id
               WHERE c.name = 'STATUS'
                 AND c.object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG')
                 AND c.max_length < 10)
    BEGIN
        ALTER TABLE dbo.TBL_CIP_CIBIL_PULL_LOG ALTER COLUMN STATUS VARCHAR(10) NOT NULL;
        PRINT 'Widened TBL_CIP_CIBIL_PULL_LOG.STATUS to varchar(10).';
    END

    -- Created only after PULL_MONTH exists (it indexes that computed column).
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CIP_PULL_LOG_EMP_MONTH'
                   AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG'))
        CREATE INDEX IX_CIP_PULL_LOG_EMP_MONTH ON dbo.TBL_CIP_CIBIL_PULL_LOG (EMP_CODE, PULL_MONTH);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CIP_PULL_LOG_DATE'
                   AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG'))
        CREATE INDEX IX_CIP_PULL_LOG_DATE ON dbo.TBL_CIP_CIBIL_PULL_LOG (PULLED_DATE);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CIP_PULL_LOG_REPORT'
                   AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG'))
        CREATE INDEX IX_CIP_PULL_LOG_REPORT ON dbo.TBL_CIP_CIBIL_PULL_LOG (REPORT_ID);
END
GO

/* --------------------------------------------- 2. report-row snapshot columns
   LOCATION_* and CENTER_MANAGER are denormalised on purpose: the grid must keep
   showing where a report was pulled from even after the employee is transferred
   or their location record is edited.

   CENTER_MANAGER holds the employee who performed the pull. Note that the
   'Centre Manager' designation (DESIG_ID 4) and the 'CENTER_HEAD' role
   (ROLE_ID 1018) both exist in master data but are held by ZERO employees, so
   neither can be used to resolve this column. See docs/CIBIL-REPORTS-SPEC.md
   section 3.5. */
IF OBJECT_ID('dbo.TBL_CIP_CIBIL_REPORTS', 'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE name = 'LOCATION_ID'
                   AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_REPORTS'))
        ALTER TABLE dbo.TBL_CIP_CIBIL_REPORTS ADD LOCATION_ID INT NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE name = 'LOCATION_NAME'
                   AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_REPORTS'))
        ALTER TABLE dbo.TBL_CIP_CIBIL_REPORTS ADD LOCATION_NAME VARCHAR(500) NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE name = 'CENTER_MANAGER'
                   AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_REPORTS'))
        ALTER TABLE dbo.TBL_CIP_CIBIL_REPORTS ADD CENTER_MANAGER VARCHAR(200) NULL;
END
GO

/* ------------------------------------------------- 3. backfill current month
   Pre-ledger history cannot be recovered exactly: we only know that a report
   row existed, not how many times each employee pulled it. Seed ONE 'BACKFILL'
   row per existing report row so the CURRENT month's numbers start from the
   true count instead of zero. Older months are deliberately not backfilled —
   they are closed and would not change any decision.

   BACKFILL is never written by the application and is not treated as a portal
   pull (it did not consume a vendor credit). */
DECLARE @thisMonth VARCHAR(7) = CONVERT(VARCHAR(7), GETDATE(), 126);

INSERT INTO dbo.TBL_CIP_CIBIL_PULL_LOG
    (REPORT_ID, PAN_NO, REPORT_TYPE, EMP_CODE, EMP_NAME,
     LOCATION_ID, LOCATION_NAME, PULLED_BY_NAME, SOURCE, STATUS, PULLED_DATE)
SELECT r.REPORT_ID, r.PAN_NO, r.REPORT_TYPE,
       ISNULL(r.PULLED_BY, 0), ISNULL(r.PULLED_BY_NAME, 'legacy-import'),
       e.LOCATION_ID, l.LOC_NAME, r.PULLED_BY_NAME, 'BACKFILL', 'OK', r.PULLED_DATE
FROM dbo.TBL_CIP_CIBIL_REPORTS r
LEFT JOIN dbo.TBL_CIP_EMPLOYEE_MASTER e ON e.EMP_CODE = r.PULLED_BY
LEFT JOIN dbo.TBL_CIP_LOCATION_MASTER  l ON l.LOC_ID  = e.LOCATION_ID
WHERE r.PULL_MONTH = @thisMonth
  AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_CIBIL_PULL_LOG x WHERE x.REPORT_ID = r.REPORT_ID);
GO

/* --------------------------------- 4. backfill location on existing reports
   Fill the snapshot columns for rows that already exist, so the new grid columns
   are populated immediately rather than only for future pulls. Legacy rows have
   PULLED_BY = NULL and stay NULL (the UI renders an em-dash). */
UPDATE r
SET r.LOCATION_ID   = e.LOCATION_ID,
    r.LOCATION_NAME = l.LOC_NAME,
    r.CENTER_MANAGER = ISNULL(r.CENTER_MANAGER, r.PULLED_BY_NAME)
FROM dbo.TBL_CIP_CIBIL_REPORTS r
JOIN dbo.TBL_CIP_EMPLOYEE_MASTER e ON e.EMP_CODE = r.PULLED_BY
LEFT JOIN dbo.TBL_CIP_LOCATION_MASTER l ON l.LOC_ID = e.LOCATION_ID
WHERE r.LOCATION_NAME IS NULL;
GO

PRINT 'CIBIL pull ledger deployed. Ledger rows this month:';
SELECT COUNT(1) AS LogRowsThisMonth
FROM dbo.TBL_CIP_CIBIL_PULL_LOG
WHERE PULL_MONTH = CONVERT(VARCHAR(7), GETDATE(), 126);
GO
