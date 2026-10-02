/* ------------------------------------------------------------------
   New Tax Regime Calculator — external sidebar link (ClearTax)
   ------------------------------------------------------------------
   Adds a top-level sidebar entry that opens
   https://cleartax.in/paytax/ in a new tab.

   MENU_PATH holds an absolute https:// URL here rather than an in-app
   route. The frontend detects that and renders a plain <a target="_blank">
   instead of a react-router NavLink (which would treat the URL as a route
   and render a blank page). Top-level, PARENT_ID NULL — sits alongside
   Dashboard / Settings. Granted to every ACTIVE role so all staff see it.

   Idempotent — safe to run repeatedly. Run on PRODUCTION.
------------------------------------------------------------------ */
SET NOCOUNT ON;

DECLARE @label  NVARCHAR(100) = N'New Tax Regime Calculator';
DECLARE @url    NVARCHAR(500) = N'https://cleartax.in/paytax/';
DECLARE @icon   NVARCHAR(50)  = N'rupee';

-- Create the menu row if it isn't already there. Matched on LABEL + PARENT_ID
-- rather than a hardcoded MENU_ID, so re-running never creates a duplicate and
-- never collides with IDs another environment has already used.
IF NOT EXISTS (
    SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER
    WHERE MENU_LABEL = @label AND (PARENT_ID IS NULL OR PARENT_ID = 0)
)
BEGIN
    INSERT INTO dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, ICON, SORT_ORDER, IS_ACTIVE)
    VALUES (@label, @url, NULL, @icon, 7, 1);   -- 7 = after Settings (SORT_ORDER 6)
END

-- Keep the row correct if it already existed (e.g. a previous partial run).
UPDATE dbo.TBL_CIP_MENU_MASTER
SET MENU_PATH = @url, ICON = @icon, IS_ACTIVE = 1
WHERE MENU_LABEL = @label AND (PARENT_ID IS NULL OR PARENT_ID = 0);

DECLARE @menuId INT = (
    SELECT MIN(MENU_ID) FROM dbo.TBL_CIP_MENU_MASTER
    WHERE MENU_LABEL = @label AND (PARENT_ID IS NULL OR PARENT_ID = 0)
);

-- Grant to every active role.
INSERT INTO dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
SELECT r.ROLE_ID, @menuId
FROM dbo.TBL_CIP_ROLE_MASTER r
WHERE r.ROLE_ACTIVE = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU x
                  WHERE x.ROLE_ID = r.ROLE_ID AND x.MENU_ID = @menuId);

-- Verification: the menu and how many roles hold it.
SELECT MENU_ID, MENU_LABEL, MENU_PATH, PARENT_ID, ICON, SORT_ORDER, IS_ACTIVE
FROM dbo.TBL_CIP_MENU_MASTER
WHERE MENU_LABEL = @label;

SELECT COUNT(*) AS active_roles, SUM(CASE WHEN rm.MENU_ID IS NOT NULL THEN 1 ELSE 0 END) AS granted
FROM dbo.TBL_CIP_ROLE_MASTER r
LEFT JOIN dbo.TBL_CIP_ROLE_MENU rm ON rm.ROLE_ID = r.ROLE_ID AND rm.MENU_ID = @menuId
WHERE r.ROLE_ACTIVE = 1;
