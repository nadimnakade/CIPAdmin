/* ------------------------------------------------------------------
   Menu grant audit — read-only diagnostic (safe to run on PRODUCTION)
   ------------------------------------------------------------------
   Answers "why can't this user see the Analyzer / that menu?" without
   changing anything. Three common causes are reported separately:

     1. Parent granted but the child menu row is missing for the role
        (this is exactly what hid CIBIL Reports from TLs).
     2. A per-employee revocation in TBL_CIP_EMPLOYEE_MENU (GRANTED = 0)
        overrides the role grant.
     3. The user's web access itself is switched off in TBL_CIP_WEB_ACCESS
        (HAS_ACCESS = 0), which blocks login altogether.

   Section 4 maps menu labels to ids so you can pick the right one.
------------------------------------------------------------------ */
SET NOCOUNT ON;

PRINT '--- 1. Menu id map ---';
SELECT MENU_ID, MENU_LABEL, MENU_PATH, PARENT_ID, ICON, IS_ACTIVE
FROM dbo.TBL_CIP_MENU_MASTER
ORDER BY MENU_ID;

PRINT '';
PRINT '--- 2. Roles holding a PARENT but missing a CHILD (the TL/menu-10 bug) ---';
SELECT parent.MENU_ID   AS parent_id,
       parent.MENU_LABEL AS parent_label,
       child.MENU_ID    AS child_id,
       child.MENU_LABEL AS child_label,
       r.ROLE_ID, r.ROLE_NAME
FROM dbo.TBL_CIP_MENU_MASTER parent
JOIN dbo.TBL_CIP_MENU_MASTER child ON child.PARENT_ID = parent.MENU_ID
CROSS JOIN dbo.TBL_CIP_ROLE_MASTER r
WHERE r.ROLE_ACTIVE = 1
  AND EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU g
              WHERE g.ROLE_ID = r.ROLE_ID AND g.MENU_ID = parent.MENU_ID)
  AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MASTER z
                  JOIN dbo.TBL_CIP_ROLE_MENU zg ON zg.ROLE_ID = z.ROLE_ID AND zg.MENU_ID = child.MENU_ID
                  WHERE z.ROLE_ID = r.ROLE_ID)
ORDER BY parent.MENU_ID, r.ROLE_ID;

PRINT '';
PRINT '--- 3. Per-employee revocations (override the role grant) ---';
SELECT em.EMP_CODE, e.EMP_NAME, em.MENU_ID, m.MENU_LABEL, em.GRANTED, em.GRANTED_ON
FROM dbo.TBL_CIP_EMPLOYEE_MENU em
LEFT JOIN dbo.TBL_CIP_EMPLOYEE_MASTER e ON e.EMP_CODE = em.EMP_CODE
LEFT JOIN dbo.TBL_CIP_MENU_MASTER m ON m.MENU_ID = em.MENU_ID
ORDER BY em.EMP_CODE, em.MENU_ID;

PRINT '';
PRINT '--- 4. Web access switched off (blocks login entirely) ---';
SELECT a.EMP_CODE, e.EMP_NAME, e.ROLE_ID, a.HAS_ACCESS, a.GRANTED_AT
FROM dbo.TBL_CIP_WEB_ACCESS a
LEFT JOIN dbo.TBL_CIP_EMPLOYEE_MASTER e ON e.EMP_CODE = a.EMP_CODE
WHERE a.HAS_ACCESS = 0;

PRINT '';
PRINT '--- 5. Who holds the Analyzer (MENU_ID 18) ---';
SELECT rm.ROLE_ID, r.ROLE_NAME, r.ROLE_ACTIVE
FROM dbo.TBL_CIP_ROLE_MENU rm
JOIN dbo.TBL_CIP_ROLE_MASTER r ON r.ROLE_ID = rm.ROLE_ID
WHERE rm.MENU_ID = 18
ORDER BY rm.ROLE_ID;
