/* ------------------------------------------------------------------
   PDF Editor — Admin-only screen under the CIBIL section
   ------------------------------------------------------------------
   Adds "PDF Editor" (/pdf-editor) under the CIBIL parent (MENU_ID 19).

   The editor rewrites CIBIL report PDFs in the browser, so it is granted
   to ADMIN ROLES ONLY (ROLE_ID 1 and 2 — HierarchyService.IsAdminRole).
   Every other active role explicitly does NOT get it; the frontend also
   hides the leaf for non-admins and guards the route, so this grant is
   the third of three layers, not the only one.

   Idempotent — safe to run repeatedly. Run on PRODUCTION.
------------------------------------------------------------------ */
SET NOCOUNT ON;

DECLARE @label NVARCHAR(100) = N'PDF Editor';
DECLARE @path  NVARCHAR(500) = N'/pdf-editor';
DECLARE @icon  NVARCHAR(50)  = N'fileText';
DECLARE @parent INT = 19;   -- CIBIL (same parent as Checker / Analyzer)

-- Create the menu row if missing. Matched on LABEL + PARENT so re-running never
-- duplicates and never collides with IDs another environment has already used.
IF NOT EXISTS (
    SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER
    WHERE MENU_LABEL = @label AND PARENT_ID = @parent
)
BEGIN
    INSERT INTO dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, ICON, SORT_ORDER, IS_ACTIVE)
    VALUES (@label, @path, @parent, @icon, 30, 1);  -- 30 = after Financial Analysis (20)
END

UPDATE dbo.TBL_CIP_MENU_MASTER
SET MENU_PATH = @path, ICON = @icon, IS_ACTIVE = 1
WHERE MENU_LABEL = @label AND PARENT_ID = @parent;

DECLARE @menuId INT = (
    SELECT MIN(MENU_ID) FROM dbo.TBL_CIP_MENU_MASTER
    WHERE MENU_LABEL = @label AND PARENT_ID = @parent
);

-- Admin roles only (1 = Super Admin, 2 = Admin).
INSERT INTO dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
SELECT r.ROLE_ID, @menuId
FROM dbo.TBL_CIP_ROLE_MASTER r
WHERE r.ROLE_ID IN (1, 2)
  AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU x
                  WHERE x.ROLE_ID = r.ROLE_ID AND x.MENU_ID = @menuId);

-- Revoke from any non-admin role that may have been granted it earlier —
-- this is the line that makes the script safe to re-run after a stray grant.
DELETE FROM dbo.TBL_CIP_ROLE_MENU
WHERE MENU_ID = @menuId AND ROLE_ID NOT IN (1, 2);

-- Verification: who can actually see it now.
SELECT m.MENU_ID, m.MENU_LABEL, m.MENU_PATH, m.PARENT_ID, m.ICON, m.SORT_ORDER, m.IS_ACTIVE
FROM dbo.TBL_CIP_MENU_MASTER m
WHERE m.MENU_ID = @menuId;

SELECT r.ROLE_ID, r.ROLE_NAME,
       CASE WHEN rm.MENU_ID IS NULL THEN 0 ELSE 1 END AS granted
FROM dbo.TBL_CIP_ROLE_MASTER r
LEFT JOIN dbo.TBL_CIP_ROLE_MENU rm ON rm.ROLE_ID = r.ROLE_ID AND rm.MENU_ID = @menuId
WHERE r.ROLE_ACTIVE = 1
ORDER BY granted DESC, r.ROLE_ID;
