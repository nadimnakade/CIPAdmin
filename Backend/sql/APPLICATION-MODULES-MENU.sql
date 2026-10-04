/* ------------------------------------------------------------------------------------------
   Application section menu (CIPAdmin)

   Ports the CIPPortal navigation for the modules rebuilt in this release:
     Loan Eligibility, EMI Calculator and the Application section
     (Search Application, single data-entry form, Scan File Upload, Bulk Insert,
      Existing Loan Details).

   The sidebar is data-driven from TBL_CIP_MENU_MASTER via /api/menus, so adding a route
   alone does not make a module visible — these rows do.

   MENU_ID is an IDENTITY column, so IDENTITY_INSERT is used to keep the ids stable and
   predictable; the role grants below depend on them.

   Idempotent: re-running leaves the same rows and the same ordering.

   Usage:  sqlcmd -S <server> -d CIP -U <user> -P <password> -b -i APPLICATION-MODULES-MENU.sql
   ------------------------------------------------------------------------------------------ */
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DECLARE @ApplicationsSection INT = 2000;

-- The new section sits third, directly under CIBIL. Existing sections are renumbered in
-- id order around it so the top-level sequence stays unique and gap-free.
IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_ID = @ApplicationsSection)
BEGIN
    /* Push every top-level section that already sits at or after slot 3 one place along. */
    UPDATE dbo.TBL_CIP_MENU_MASTER
       SET SORT_ORDER = SORT_ORDER + 1
     WHERE PARENT_ID IS NULL
       AND SORT_ORDER >= 3;
END

DECLARE @Items TABLE (MENU_ID INT, MENU_LABEL VARCHAR(100), MENU_PATH VARCHAR(200), ICON VARCHAR(50), SORT_ORDER INT);

INSERT INTO @Items (MENU_ID, MENU_LABEL, MENU_PATH, ICON, SORT_ORDER) VALUES
    (2001, N'Loan Eligibility',   N'/loan-eligibility',           N'rupee',    1),
    (2002, N'EMI Calculator',     N'/emi-calculator',             N'wallet',   2),
    (2003, N'Search Application', N'/applications',               N'search',   3),
    (2004, N'New Application',    N'/applications/new',           N'plus',     4),
    (2005, N'Scan File Upload',   N'/applications/scans',         N'upload',   5),
    (2006, N'Bulk Insert',        N'/applications/bulk',          N'database', 6),
    (2007, N'Existing Loans',     N'/applications/existing-loans',N'wallet',   7);

SET IDENTITY_INSERT dbo.TBL_CIP_MENU_MASTER ON;

IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_ID = @ApplicationsSection)
    INSERT INTO dbo.TBL_CIP_MENU_MASTER (MENU_ID, MENU_LABEL, MENU_PATH, PARENT_ID, ICON, SORT_ORDER, IS_ACTIVE)
    VALUES (@ApplicationsSection, 'Applications', NULL, NULL, 'briefcase', 3, 1);

INSERT INTO dbo.TBL_CIP_MENU_MASTER (MENU_ID, MENU_LABEL, MENU_PATH, PARENT_ID, ICON, SORT_ORDER, IS_ACTIVE)
SELECT i.MENU_ID, i.MENU_LABEL, i.MENU_PATH, @ApplicationsSection, i.ICON, i.SORT_ORDER, 1
  FROM @Items i
 WHERE NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER m WHERE m.MENU_ID = i.MENU_ID);

/* Refresh labels/paths in case a row already existed with an older spelling. */
UPDATE m
   SET m.MENU_LABEL = i.MENU_LABEL,
       m.MENU_PATH  = i.MENU_PATH,
       m.PARENT_ID  = @ApplicationsSection,
       m.ICON       = i.ICON,
       m.SORT_ORDER = i.SORT_ORDER,
       m.IS_ACTIVE  = 1
  FROM dbo.TBL_CIP_MENU_MASTER m
  JOIN @Items i ON i.MENU_ID = m.MENU_ID;

SET IDENTITY_INSERT dbo.TBL_CIP_MENU_MASTER OFF;

/* Grants.
   Loan Eligibility and the EMI Calculator are read-only tools that touch nothing but
   reference tables, so they go to every role. The Application section reads and writes
   customer applications, so it is limited to the roles that already handle applications:
   SUPER ADMIN, ADMIN, OPS, TL, DATA ENTRY and CIP ENTRY. */
INSERT INTO dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
SELECT r.ROLE_ID, m.MENU_ID
  FROM dbo.TBL_CIP_ROLE_MASTER r
  CROSS JOIN (VALUES (2001), (2002)) AS t(MENU_ID)
  JOIN dbo.TBL_CIP_MENU_MASTER m ON m.MENU_ID = t.MENU_ID
 WHERE NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU rm WHERE rm.ROLE_ID = r.ROLE_ID AND rm.MENU_ID = t.MENU_ID);

DECLARE @AppRoles TABLE (ROLE_ID INT);
INSERT INTO @AppRoles (ROLE_ID) VALUES (1), (2), (3), (4), (1008), (2012);

INSERT INTO dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
SELECT r.ROLE_ID, t.MENU_ID
  FROM @AppRoles r
  CROSS JOIN (VALUES (2000), (2003), (2004), (2005), (2006), (2007)) AS t(MENU_ID)
 WHERE NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU rm WHERE rm.ROLE_ID = r.ROLE_ID AND rm.MENU_ID = t.MENU_ID);

COMMIT TRANSACTION;

/* Verification: the new tree, then the top-level ordering it produced. */
SELECT m.MENU_ID, m.MENU_LABEL, m.MENU_PATH, m.PARENT_ID, m.SORT_ORDER
  FROM dbo.TBL_CIP_MENU_MASTER m
 WHERE m.MENU_ID BETWEEN 2000 AND 2007
 ORDER BY m.MENU_ID;

SELECT MENU_ID, MENU_LABEL, MENU_PATH, SORT_ORDER
  FROM dbo.TBL_CIP_MENU_MASTER
 WHERE PARENT_ID IS NULL
 ORDER BY SORT_ORDER;