/* ------------------------------------------------------------------------------------------
   Punch In / Out menu (CIPAdmin)

   Makes the ported PunchIn module reachable from the data-driven sidebar. The legacy portal
   carried Punch In/Out as its own top-level "Attendance" area; this reproduces that as a
   small section so it is not buried under Masters.

   MENU_ID is an IDENTITY column, so IDENTITY_INSERT keeps the ids stable for the grants.

   Idempotent: re-running leaves the same rows and the same ordering.

   Usage:  sqlcmd -S <server> -d CIP -U <user> -P <password> -b -i PUNCHIN-MENU.sql
   ------------------------------------------------------------------------------------------ */
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DECLARE @Section INT = 3000;
DECLARE @Leaf    INT = 3001;

/* Append after the current last top-level section so no existing ordering shifts. */
DECLARE @Slot INT = (
    SELECT ISNULL(MAX(SORT_ORDER), 0) + 1
    FROM dbo.TBL_CIP_MENU_MASTER
    WHERE PARENT_ID IS NULL
);

IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_ID = @Section)
BEGIN
    UPDATE dbo.TBL_CIP_MENU_MASTER SET SORT_ORDER = @Slot WHERE MENU_ID = @Section;
    UPDATE dbo.TBL_CIP_MENU_MASTER SET SORT_ORDER = @Slot + 1 WHERE MENU_ID = @Leaf;
END

SET IDENTITY_INSERT dbo.TBL_CIP_MENU_MASTER ON;

IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_ID = @Section)
    INSERT INTO dbo.TBL_CIP_MENU_MASTER (MENU_ID, MENU_LABEL, MENU_PATH, PARENT_ID, ICON, SORT_ORDER, IS_ACTIVE)
    VALUES (@Section, 'Attendance', NULL, NULL, 'clock', @Slot, 1);

IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_ID = @Leaf)
    INSERT INTO dbo.TBL_CIP_MENU_MASTER (MENU_ID, MENU_LABEL, MENU_PATH, PARENT_ID, ICON, SORT_ORDER, IS_ACTIVE)
    VALUES (@Leaf, 'Punch In / Out', '/punch-in', @Section, 'check', @Slot + 1, 1);

UPDATE dbo.TBL_CIP_MENU_MASTER
   SET MENU_LABEL = 'Attendance', MENU_PATH = NULL, PARENT_ID = NULL,
       ICON = 'clock', IS_ACTIVE = 1
 WHERE MENU_ID = @Section;

UPDATE dbo.TBL_CIP_MENU_MASTER
   SET MENU_LABEL = 'Punch In / Out', MENU_PATH = '/punch-in', PARENT_ID = @Section,
       ICON = 'check', IS_ACTIVE = 1
 WHERE MENU_ID = @Leaf;

SET IDENTITY_INSERT dbo.TBL_CIP_MENU_MASTER OFF;

/* Punching attendance is a field-supervisor duty: SUPER ADMIN, ADMIN, OPS, TL and the
   operations variants. It writes to TBL_CIP_ATTENDANCE_MASTER, so it is not granted to
   caller-facing roles. */
DECLARE @Roles TABLE (ROLE_ID INT);
INSERT INTO @Roles (ROLE_ID) VALUES (1), (2), (3), (4), (1006), (1007), (1009), (2010), (2011);

INSERT INTO dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
SELECT r.ROLE_ID, t.MENU_ID
  FROM @Roles r
  CROSS JOIN (VALUES (3000), (3001)) AS t(MENU_ID)
 WHERE NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU rm WHERE rm.ROLE_ID = r.ROLE_ID AND rm.MENU_ID = t.MENU_ID);

COMMIT TRANSACTION;

/* Verification */
SELECT MENU_ID, MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER, IS_ACTIVE
  FROM dbo.TBL_CIP_MENU_MASTER
 WHERE MENU_ID IN (3000, 3001)
 ORDER BY MENU_ID;