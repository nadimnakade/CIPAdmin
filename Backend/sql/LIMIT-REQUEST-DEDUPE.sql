/* One pending limit request per employee per month.
   ----------------------------------------------------------------------------
   Why this exists
     CibilCheckerPage blocks a double submit in the browser, but the UI guard is
     not a guarantee: it is bypassed by two tabs, by a slow first response with
     the panel still open, or by any direct call to POST /api/cibil/limit-request.
     The service already checks for an existing Pending row, but that is a
     read-then-write with no lock, so two requests arriving together both see
     "no pending request" and both INSERT. This index makes the second insert
     fail at the database instead of silently creating a duplicate row that the
     administrator then sees twice.

   The index is FILTERED on STATUS = 'Pending', so approving or rejecting a
   request frees the slot and a later request for the same month is allowed.

   Run this BEFORE deploying the frontend fix; the app catches the duplicate-key
   error and reports it as a normal 400 "you already have a pending request".
*/
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF OBJECT_ID('dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS', 'U') IS NULL
BEGIN
    PRINT 'SKIP: dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS does not exist.';
    RETURN;
END

/* Report duplicates BEFORE adding the index - the CREATE would fail if any exist,
   and an operator needs to see which rows to clean up first. */
DECLARE @dupes TABLE
(
    EMP_CODE       INT,
    REQUEST_MONTH  NVARCHAR(20),
    DUP_COUNT      INT
);

INSERT INTO @dupes (EMP_CODE, REQUEST_MONTH, DUP_COUNT)
SELECT EMP_CODE, REQUEST_MONTH, COUNT(*)
FROM dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS
WHERE STATUS = 'Pending'
GROUP BY EMP_CODE, REQUEST_MONTH
HAVING COUNT(*) > 1;

IF EXISTS (SELECT 1 FROM @dupes)
BEGIN
    PRINT 'DUPLICATE PENDING REQUESTS FOUND - index NOT created.';
    PRINT 'Resolve these first (approve/reject the extras), then re-run this script:';
    SELECT EMP_CODE, REQUEST_MONTH, DUP_COUNT FROM @dupes ORDER BY DUP_COUNT DESC;
    RETURN;
END

IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS')
             AND name = 'UX_CIBIL_LIMIT_REQ_PENDING')
BEGIN
    PRINT 'OK: UX_CIBIL_LIMIT_REQ_PENDING already exists - nothing to do.';
    RETURN;
END

CREATE UNIQUE INDEX UX_CIBIL_LIMIT_REQ_PENDING
    ON dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS (EMP_CODE, REQUEST_MONTH)
    WHERE STATUS = 'Pending';

PRINT 'Created UX_CIBIL_LIMIT_REQ_PENDING.';