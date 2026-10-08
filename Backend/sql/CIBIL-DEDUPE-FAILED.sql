/* ============================================================================
   CIBIL dedup by pull ATTEMPT, not by report artefact.

   The old freeze ("one pull per PAN per month") was enforced against
   TBL_CIP_CIBIL_REPORTS, which only ever gets a row after a SUCCESSFUL pull.
   A failed vendor call wrote nothing there, so the next Generate for that PAN
   looked "new" and called the vendor again — every retry burning a credit even
   though nothing was ever produced. The failures were recorded in
   TBL_CIP_CIBIL_PULL_LOG (STATUS='FAILED') but never consulted.

   This script makes the ledger the dedup source. No new table:

   1. ERROR_MESSAGE  — the vendor's error text, captured on a failed attempt so
                       an administrator can see WHY the earlier pull died.
                       Exposed to non-admins never (see controller redaction).
   2. IX_CIP_PULL_LOG_PAN_MONTH — the dedup lookup is
                       WHERE PAN_NO=@pan AND REPORT_TYPE=@type AND PULL_MONTH=@m;
                       without this index every Generate table-scans the ledger.
   3. UX_CIP_PULL_INFLIGHT — at most ONE PENDING (in-flight) attempt per PAN+type,
                       so two concurrent Generate clicks cannot both reach the
                       vendor. The application inserts STATUS='PENDING' before
                       calling the vendor and finalises to OK/FAILED afterwards;
                       a losing insert gets SQL 2601/2627 and is reported as
                       "pull already in progress".

   SQL Server 2014 compatible. Idempotent — safe to run more than once.
   Run after Backend/sql/CIBIL-PULL-LOG.sql.
   ============================================================================ */

SET NOCOUNT ON;
GO

/* ---------------------------------------------------- 1. failure reason ---- */
IF OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE name = 'ERROR_MESSAGE'
                     AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG'))
BEGIN
    ALTER TABLE dbo.TBL_CIP_CIBIL_PULL_LOG ADD ERROR_MESSAGE VARCHAR(500) NULL;
    PRINT 'Added TBL_CIP_CIBIL_PULL_LOG.ERROR_MESSAGE.';
END
GO

/* ------------------------------------------- 2. dedup lookup index --------- */
/* Both indexes below need QUOTED_IDENTIFIER ON: IX keys on the computed PULL_MONTH
   column and UX is a filtered index. sqlcmd defaults it to OFF, and the setting
   persists across GO for the rest of the script (same pattern as CIBIL-PULL-LOG.sql). */
SET QUOTED_IDENTIFIER ON;
GO
IF OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_CIP_PULL_LOG_PAN_MONTH'
                     AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG'))
BEGIN
    CREATE INDEX IX_CIP_PULL_LOG_PAN_MONTH
        ON dbo.TBL_CIP_CIBIL_PULL_LOG (PAN_NO, REPORT_TYPE, PULL_MONTH)
        INCLUDE (STATUS, REPORT_ID, EMP_NAME, PULLED_DATE, ERROR_MESSAGE);
    PRINT 'Created IX_CIP_PULL_LOG_PAN_MONTH.';
END
GO

/* ------------------------------- 3. one in-flight vendor call per PAN ------ */
IF OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'UX_CIP_PULL_INFLIGHT'
                     AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG'))
BEGIN
    CREATE UNIQUE INDEX UX_CIP_PULL_INFLIGHT
        ON dbo.TBL_CIP_CIBIL_PULL_LOG (PAN_NO, REPORT_TYPE)
        WHERE STATUS = 'PENDING';
    PRINT 'Created UX_CIP_PULL_INFLIGHT.';
END
GO

/* A PENDING row means "vendor call in progress right now". Rows left behind by a
   crashed/restarted process would otherwise freeze their PAN forever AND occupy
   the in-flight slot, so sweep anything older than 15 minutes to FAILED. The
   application applies the same sweep before inserting, but this keeps a fresh
   deploy clean even for crashes that predate it. */
IF OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG', 'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns
               WHERE name = 'ERROR_MESSAGE'
                 AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_PULL_LOG'))
    UPDATE dbo.TBL_CIP_CIBIL_PULL_LOG
       SET STATUS = 'FAILED',
           ERROR_MESSAGE = COALESCE(ERROR_MESSAGE, 'Pull attempt interrupted (server restart or timeout).')
     WHERE STATUS = 'PENDING'
       AND PULLED_DATE < DATEADD(MINUTE, -15, GETDATE());
GO

PRINT 'CIBIL attempt dedup deployed.';
SELECT COUNT(1) AS LogRowsThisMonth
FROM dbo.TBL_CIP_CIBIL_PULL_LOG
WHERE PULL_MONTH = CONVERT(VARCHAR(7), GETDATE(), 126);
GO
