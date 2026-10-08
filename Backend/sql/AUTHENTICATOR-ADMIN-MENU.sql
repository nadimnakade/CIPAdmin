/* =============================================================================
   CIP Admin - "Authenticator Setup" admin menu

   Adds the screen that manages the Google Authenticator second factor:
   who must use it (IS_BYPASS = 0) and who is enrolled. Replaces the legacy
   CIPPortal Verify/ResetGoogleAuth screen.

   Idempotent: safe to run on a server that already has it.
============================================================================= */

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @MenuId INT = 2100;
DECLARE @Label VARCHAR(100) = 'Authenticator Setup';
DECLARE @Path VARCHAR(200) = '/authenticator-setup';
DECLARE @Icon VARCHAR(50) = 'shield';

/* Hang it under Administration, next to Menu Master and Role & Privilege Master, which
   is where every other admin-only screen lives. A top-level row would strand it on its
   own in the sidebar, separated from the other administration tools. Resolved by label
   rather than hard-coded id so a differently-numbered server still works. */
DECLARE @ParentId INT = (SELECT TOP 1 MENU_ID FROM dbo.TBL_CIP_MENU_MASTER
                          WHERE MENU_LABEL = 'Administration' AND PARENT_ID IS NULL
                          ORDER BY MENU_ID);
/* Sort last among the siblings, EXCLUDING this menu itself. Excluding it is what makes
   the script idempotent: if the row already exists, MAX over the other children still
   yields the same position instead of pushing the menu down one slot on every run. */
DECLARE @SortOrder INT = (SELECT ISNULL(MAX(SORT_ORDER), 0) + 1 FROM dbo.TBL_CIP_MENU_MASTER
                           WHERE PARENT_ID = @ParentId AND MENU_ID <> @MenuId);

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_ID = @MenuId)
    BEGIN
        SET IDENTITY_INSERT dbo.TBL_CIP_MENU_MASTER ON;
        INSERT INTO dbo.TBL_CIP_MENU_MASTER (MENU_ID, MENU_LABEL, MENU_PATH, PARENT_ID, ICON, SORT_ORDER, IS_ACTIVE)
        VALUES (@MenuId, @Label, @Path, @ParentId, @Icon, @SortOrder, 1);
        SET IDENTITY_INSERT dbo.TBL_CIP_MENU_MASTER OFF;
    END
    ELSE
    BEGIN
        -- Keep the row in step with the code if the label, path or placement ever moved.
        UPDATE dbo.TBL_CIP_MENU_MASTER
           SET MENU_LABEL = @Label, MENU_PATH = @Path, PARENT_ID = @ParentId,
               ICON = @Icon, SORT_ORDER = @SortOrder, IS_ACTIVE = 1
         WHERE MENU_ID = @MenuId;
    END

    /* Only admins can manage the second factor - the same roles the API allows
       (SUPER ADMIN 1, ADMIN 2). Granting it wider would expose a screen whose
       endpoint returns 403 to everyone else. */
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
END CATCH;

/* Verification */
SELECT m.MENU_ID, m.MENU_LABEL, m.MENU_PATH, p.MENU_LABEL AS PARENT, m.ICON, m.SORT_ORDER, m.IS_ACTIVE
  FROM dbo.TBL_CIP_MENU_MASTER m
  LEFT JOIN dbo.TBL_CIP_MENU_MASTER p ON p.MENU_ID = m.PARENT_ID
 WHERE m.MENU_ID = @MenuId;

-- The sibling screens under Administration, so the placement is visible at a glance.
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
