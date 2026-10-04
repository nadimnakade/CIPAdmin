/* ============================================================================
   Single active session per employee + idle logout.

   Why this exists
   ---------------
   Two separate requirements share one mechanism, because both are statements
   about "is this employee currently signed in, and how recently did they do
   something?":

     1. No multiple logins. A JWT is self-contained and stateless, so until now
        the same employee could sign in from any number of browsers and every
        token stayed valid until its 8-hour `exp`. Nothing tied a token back to
        a login event. This table holds exactly ONE row per employee (EMP_CODE
        is the primary key), stamped with the `jti` of the token that login
        issued. A new login overwrites the row, which invalidates the previous
        token on the next request.

     2. 15-minute idle timeout. LAST_SEEN is refreshed on every authenticated
        request. A request whose LAST_SEEN is older than Session:IdleTimeoutMinutes
        is rejected and the row is removed, so an abandoned tab cannot be resumed.

   The row is the authority. It is deliberately NOT cached in process memory:
   the IIS app pool may run several worker processes, and an in-memory session
   table would let a user be "signed in" on one worker while another worker
   rejected them. One indexed primary-key lookup per request is cheap.

   Interaction with the JWT `exp` (Jwt:ExpiryMinutes, 480): the token lifetime
   stays the hard outer ceiling, and the idle window is the inner one. A user
   active continuously is still logged out after 8 hours; a user idle for
   15 minutes is logged out long before that.

   SQL Server 2014 compatible. Idempotent — safe to run more than once.

   Deploy note: until this script is run, the application behaves exactly as
   before (no single-login enforcement, no idle logout). Session checks fail
   open if the table is missing so a forgotten deploy cannot lock every user
   out of the portal.
   ============================================================================ */

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.TBL_CIP_ACTIVE_SESSION', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_ACTIVE_SESSION (
        EMP_CODE    INT         NOT NULL CONSTRAINT PK_CIP_ACTIVE_SESSION PRIMARY KEY,
        JTI         VARCHAR(64) NOT NULL,  -- the `jti` claim of the currently valid token
        LOGIN_TIME  DATETIME    NOT NULL CONSTRAINT DF_CIP_SESSION_LOGIN DEFAULT (GETDATE()),
        LAST_SEEN   DATETIME    NOT NULL CONSTRAINT DF_CIP_SESSION_SEEN  DEFAULT (GETDATE()),
        IP_ADDRESS  VARCHAR(45) NULL,
        USER_AGENT  VARCHAR(400) NULL
    );

    PRINT 'TBL_CIP_ACTIVE_SESSION created.';
END
ELSE
    PRINT 'TBL_CIP_ACTIVE_SESSION already exists — skipped.';
GO

/* JTI is looked up on its own when a request presents a token, so it needs an
   index. EMP_CODE is already covered by the primary key. */
IF OBJECT_ID('dbo.TBL_CIP_ACTIVE_SESSION', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_CIP_ACTIVE_SESSION_JTI'
                     AND object_id = OBJECT_ID('dbo.TBL_CIP_ACTIVE_SESSION'))
    CREATE INDEX IX_CIP_ACTIVE_SESSION_JTI ON dbo.TBL_CIP_ACTIVE_SESSION (JTI);
GO

/* Rows older than a full token lifetime can never be matched by a live token
   (the JWT `exp` would have passed). Clearing them keeps the table tiny. This
   is only housekeeping — correctness does not depend on it, because a stale
   row is overwritten by the next login anyway. */
DELETE FROM dbo.TBL_CIP_ACTIVE_SESSION
WHERE LAST_SEEN < DATEADD(DAY, -2, GETDATE());
GO

PRINT 'Single-session + idle-logout support deployed.';
SELECT COUNT(1) AS ActiveSessions FROM dbo.TBL_CIP_ACTIVE_SESSION;
GO
