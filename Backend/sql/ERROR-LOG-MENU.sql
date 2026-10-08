/* =============================================================================
   CIP Admin - "Error Log" admin menu

   Adds the screen that lists every database / application error the API recorded in
   dbo.TBL_CIP_API_ERROR_LOG, with server-side pagination, filters and triage.

   Run AFTER Backend/sql/ERROR-LOG.sql (the screen needs the table) and AFTER
   Backend/sql/AUTHENTICATOR-ADMIN-MENU.sql.

   Idempotent: safe to run on a server that already has it.

   WHY THE MENU ID IS RESOLVED AT RUN TIME
   ---------------------------------------
   This script used to hardcode MENU_ID 2101 and branch on "does that ID already
   exist?". On the production server 2101 is ALREADY TAKEN by a different,
   unrelated menu, so that branch took the UPDATE path and would have renamed,
   re-parented and re-iconned a live screen - and that screen is granted to roles
   1 and 2, so it would have disappeared from every administrator's sidebar.

   So the row is now located BY ITS PATH, which is the one thing that actually
   identifies this menu, and the ID is only used for the INSERT. If the preferred
   ID is occupied by something else, a free one is allocated and the script says
   so. Nothing that is not already '/error-logs' is ever modified.
   ============================================================================= */

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @Label VARCHAR(100) = 'Error Log';
DECLARE @Path VARCHAR(200) = '/error-logs';
DECLARE @Icon VARCHAR(50) = 'alert';
DECLARE @PreferredId INT = 2101;

/* Under Administration, beside the other admin-only screens. The rows carry stack
   traces, SQL messages and client IP addresses, so this belongs with the other
   administration tools rather than as a top-level sidebar entry. */
DECLARE @ParentId INT = (SELECT TOP 1 MENU_ID FROM dbo.TBL_CIP_MENU_MASTER
                          WHERE MENU_LABEL = 'Administration' AND PARENT_ID IS NULL
                          ORDER BY MENU_ID);

IF @ParentId IS NULL
BEGIN
    PRINT 'ERROR: no top-level ''Administration'' menu found in dbo.TBL_CIP_MENU_MASTER.';
    PRINT 'The Error Log screen is admin-only, so it is filed under Administration.';
    PRINT 'Add that parent menu first, then run this script again.';
    THROW 50001, 'Administration parent menu not found', 1;
END

/* The existing Error Log row, found BY PATH. This is the only trustworthy
   identity for the menu - the numeric ID is not, because a different screen can
   legitimately own it on a server this script has never run on before. */
DECLARE @ExistingId INT = (SELECT TOP 1 MENU_ID FROM dbo.TBL_CIP_MENU_MASTER
                            WHERE MENU_PATH = @Path
                            ORDER BY MENU_ID);

/* Only used for the INSERT. Keep the preferred ID when it is genuinely free or
   already ours; otherwise take the next free one above everything in the 2xxx
   block, which is where the admin-only screens live (2100 = Authenticator Setup). */
DECLARE @MenuId INT =
    COALESCE(@ExistingId,
             CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER
                                    WHERE MENU_ID = @PreferredId)
                  THEN @PreferredId
                  ELSE (SELECT ISNULL(MAX(MENU_ID), @PreferredId) + 1
                        FROM dbo.TBL_CIP_MENU_MASTER
                        WHERE MENU_ID >= @PreferredId)
             END);

/* Sort last among the siblings, EXCLUDING this menu itself. Excluding it is what
   makes the script idempotent: if the row already exists, MAX over the OTHER
   children still yields the same position instead of pushing the menu down a slot
   every run. */
DECLARE @SortOrder INT = (SELECT ISNULL(MAX(SORT_ORDER), 0) + 1 FROM dbo.TBL_CIP_MENU_MASTER
                           WHERE PARENT_ID = @ParentId AND MENU_ID <> @MenuId);

IF @ExistingId IS NULL AND @MenuId <> @PreferredId
BEGIN
    PRINT 'NOTE: MENU_ID ' + CAST(@PreferredId AS VARCHAR(10)) + ' is already used on this server,';
    PRINT '      so the Error Log menu will be created as MENU_ID ' + CAST(@MenuId AS VARCHAR(10)) + '.';
    PRINT '      Nothing else is modified. Here is what holds the preferred ID:';
    SELECT m.MENU_ID, m.MENU_LABEL, m.MENU_PATH, p.MENU_LABEL AS PARENT, m.ICON, m.SORT_ORDER
      FROM dbo.TBL_CIP_MENU_MASTER m
      LEFT JOIN dbo.TBL_CIP_MENU_MASTER p ON p.MENU_ID = m.PARENT_ID
     WHERE m.MENU_ID = @PreferredId;
END

BEGIN TRY
    BEGIN TRANSACTION;

    /* Keyed on the row we actually own (found by path), never on the preferred
       ID - this is the branch that used to overwrite a foreign menu. */
    IF @ExistingId IS NOT NULL
    BEGIN
        UPDATE dbo.TBL_CIP_MENU_MASTER
           SET MENU_LABEL = @Label, MENU_PATH = @Path, PARENT_ID = @ParentId,
               ICON = @Icon, SORT_ORDER = @SortOrder, IS_ACTIVE = 1
         WHERE MENU_ID = @ExistingId;
    END
    ELSE
    BEGIN
        SET IDENTITY_INSERT dbo.TBL_CIP_MENU_MASTER ON;
        INSERT INTO dbo.TBL_CIP_MENU_MASTER (MENU_ID, MENU_LABEL, MENU_PATH, PARENT_ID, ICON, SORT_ORDER, IS_ACTIVE)
        VALUES (@MenuId, @Label, @Path, @ParentId, @Icon, @SortOrder, 1);
        SET IDENTITY_INSERT dbo.TBL_CIP_MENU_MASTER OFF;
    END

    /* ADMIN ONLY. The endpoint is [Authorize(Policy = "Admin")] (roles 1 and 2), so
       granting this menu to any other role would show a screen that always answers
       403 - worse than not showing it at all. */
    INSERT INTO dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
    SELECT r.ROLE_ID, @MenuId
      FROM dbo.TBL_CIP_ROLE_MASTER r
     WHERE r.ROLE_ID IN (1, 2)
       AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU rm
                        WHERE rm.ROLE_ID = r.ROLE_ID AND rm.MENU_ID = @MenuId);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH

/* The screen is useless without the table, so say so here rather than letting an
   operator discover it as an empty grid. */
IF OBJECT_ID('dbo.TBL_CIP_API_ERROR_LOG', 'U') IS NULL
    PRINT 'WARNING: menu added but dbo.TBL_CIP_API_ERROR_LOG is missing. Run Backend/sql/ERROR-LOG.sql or the screen will show a setup warning instead of errors.';
ELSE
    PRINT 'TBL_CIP_API_ERROR_LOG present.';

/* Verification */
SELECT m.MENU_ID, m.MENU_LABEL, m.MENU_PATH, p.MENU_LABEL AS PARENT, m.ICON, m.SORT_ORDER, m.IS_ACTIVE
  FROM dbo.TBL_CIP_MENU_MASTER m
  LEFT JOIN dbo.TBL_CIP_MENU_MASTER p ON p.MENU_ID = m.PARENT_ID
 WHERE m.MENU_PATH = @Path;

SELECT MENU_ID, MENU_LABEL, MENU_PATH, SORT_ORDER
  FROM dbo.TBL_CIP_MENU_MASTER
 WHERE PARENT_ID = @ParentId
 ORDER BY SORT_ORDER, MENU_ID;

SELECT r.ROLE_ID, r.ROLE_NAME,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU rm
                          WHERE rm.ROLE_ID = r.ROLE_ID AND rm.MENU_ID = @MenuId)
            THEN 'granted' ELSE 'MISSING' END AS GRANT_STATE
  FROM dbo.TBL_CIP_ROLE_MASTER r
 WHERE r.ROLE_ID IN (1, 2)
 ORDER BY r.ROLE_ID;