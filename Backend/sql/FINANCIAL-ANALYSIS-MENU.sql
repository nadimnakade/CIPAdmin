/* ------------------------------------------------------------------
   Financial Analysis — new standalone screen
   ------------------------------------------------------------------
   Adds "Financial Analysis" under the CIBIL parent (MENU_ID 19). It is a
   SEPARATE screen from the existing "Analyzer" (MENU_ID 18) and is
   intentionally both uploaded and granted, so granting this one never
   removes access to the original analyzer.

   The screen accepts a bank statement, a CIBIL report, or both.

   Idempotent — safe to run repeatedly. Run on PRODUCTION.
------------------------------------------------------------------ */
SET NOCOUNT ON;

DECLARE @label NVARCHAR(100) = N'Financial Analysis';
DECLARE @path NVARCHAR(500) = N'/financial-analysis';
DECLARE @icon NVARCHAR(50)  = N'barChart';
DECLARE @parent INT = 19;   -- CIBIL

-- Create the menu row if missing. Matched on LABEL + PARENT so re-running never
-- duplicates and never collides with IDs another environment has already used.
IF NOT EXISTS (
    SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER
    WHERE MENU_LABEL = @label AND PARENT_ID = @parent
)
BEGIN
    INSERT INTO dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, ICON, SORT_ORDER, IS_ACTIVE)
    VALUES (@label, @path, @parent, @icon, 20, 1);  -- 20 = after Analyzer (18)
END

UPDATE dbo.TBL_CIP_MENU_MASTER
SET MENU_PATH = @path, ICON = @icon, IS_ACTIVE = 1
WHERE MENU_LABEL = @label AND PARENT_ID = @parent;

DECLARE @menuId INT = (
    SELECT MIN(MENU_ID) FROM dbo.TBL_CIP_MENU_MASTER
    WHERE MENU_LABEL = @label AND PARENT_ID = @parent
);

-- Grant to every active role, mirroring the existing Analyzer's coverage.
INSERT INTO dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
SELECT r.ROLE_ID, @menuId
FROM dbo.TBL_CIP_ROLE_MASTER r
WHERE r.ROLE_ACTIVE = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU x
                  WHERE x.ROLE_ID = r.ROLE_ID AND x.MENU_ID = @menuId);

-- Verification: both the new screen and the original Analyzer are present.
SELECT m.MENU_ID, m.MENU_LABEL, m.MENU_PATH, m.PARENT_ID, m.ICON, m.SORT_ORDER, m.IS_ACTIVE
FROM dbo.TBL_CIP_MENU_MASTER m
WHERE m.MENU_ID = @menuId OR m.MENU_ID = 18;

SELECT COUNT(*) AS active_roles,
       SUM(CASE WHEN rm.MENU_ID IS NOT NULL THEN 1 ELSE 0 END) AS granted
FROM dbo.TBL_CIP_ROLE_MASTER r
LEFT JOIN dbo.TBL_CIP_ROLE_MENU rm ON rm.ROLE_ID = r.ROLE_ID AND rm.MENU_ID = @menuId
WHERE r.ROLE_ACTIVE = 1;
