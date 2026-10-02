/* ------------------------------------------------------------------
   CIBIL Reports — menu access for TLs
   ------------------------------------------------------------------
   Symptom: only admins could open Reports > CIBIL Reports. Team
   Leaders (ROLE_ID 4) had the "Reports" parent (MENU_ID 7) but not
   its "CIBIL Reports" child (MENU_ID 10), so the sidebar rendered an
   empty Reports group and /reports was unreachable from the menu.

   Fix: grant MENU_ID 10 to every non-admin role that already has the
   Reports parent (7) and the CIBIL parent (19), so the Reports page
   lines up with what the TL is expected to see. Admins already had it.
   Centre Manager is the CENTER_HEAD role (ROLE_ID 2018) and is
   included by the same rule.

   Idempotent — safe to run repeatedly. Run on PRODUCTION before
   deploying the new build.
------------------------------------------------------------------ */
SET NOCOUNT ON;

-- Report which roles are missing the child menu before we touch anything.
SELECT r.ROLE_ID, r.ROLE_NAME
FROM dbo.TBL_CIP_ROLE_MASTER r
JOIN dbo.TBL_CIP_ROLE_MENU rm ON rm.ROLE_ID = r.ROLE_ID AND rm.MENU_ID = 7   -- Reports parent
WHERE r.ROLE_ACTIVE = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MASTER x
                  JOIN dbo.TBL_CIP_ROLE_MENU xm ON xm.ROLE_ID = x.ROLE_ID AND xm.MENU_ID = 10
                  WHERE x.ROLE_ID = r.ROLE_ID);

-- Grant CIBIL Reports (10) to every active role that has the Reports parent (7)
-- but not the child yet.
INSERT INTO dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
SELECT DISTINCT rm.ROLE_ID, 10
FROM dbo.TBL_CIP_ROLE_MASTER rm2
JOIN dbo.TBL_CIP_ROLE_MASTER rm ON rm.ROLE_ID = rm2.ROLE_ID
JOIN dbo.TBL_CIP_ROLE_MENU g ON g.ROLE_ID = rm.ROLE_ID AND g.MENU_ID = 7
WHERE rm.ROLE_ACTIVE = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU e
                  WHERE e.ROLE_ID = rm.ROLE_ID AND e.MENU_ID = 10);

-- Verification: every active role holding the Reports parent now holds the child.
SELECT r.ROLE_ID, r.ROLE_NAME,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU x
                         WHERE x.ROLE_ID = r.ROLE_ID AND x.MENU_ID = 10)
            THEN 'OK' ELSE 'MISSING' END AS cibil_reports_menu
FROM dbo.TBL_CIP_ROLE_MASTER r
JOIN dbo.TBL_CIP_ROLE_MENU rm ON rm.ROLE_ID = r.ROLE_ID AND rm.MENU_ID = 7
WHERE r.ROLE_ACTIVE = 1
ORDER BY r.ROLE_ID;
