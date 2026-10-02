-- TBL_CIP_APP_SETTINGS — run once on the server DB (CIP) if missing.
-- Idempotent: safe to run even if the table already exists.
-- Fixes: 500 on Settings → "Save limit" (PUT /api/settings/monthly-pull-limit).

IF OBJECT_ID(N'dbo.TBL_CIP_APP_SETTINGS', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_APP_SETTINGS
    (
        SETTING_KEY   varchar(100)  NOT NULL CONSTRAINT PK_CIP_APP_SETTINGS PRIMARY KEY,
        SETTING_VALUE nvarchar(500) NOT NULL,
        UPDATED_BY    int           NULL,
        UPDATED_ON    datetime      NOT NULL CONSTRAINT DF_CIP_APP_SETTINGS_UPDATED_ON DEFAULT (GETDATE())
    );

    -- Seed the default monthly CIBIL pull limit
    INSERT INTO dbo.TBL_CIP_APP_SETTINGS (SETTING_KEY, SETTING_VALUE, UPDATED_BY)
    VALUES ('Cibil.MonthlyPullLimit', '50', NULL);
END
GO

-- If the table existed but lacks the newer columns, add them.
IF COL_LENGTH('dbo.TBL_CIP_APP_SETTINGS', 'UPDATED_BY') IS NULL
    ALTER TABLE dbo.TBL_CIP_APP_SETTINGS ADD UPDATED_BY int NULL;
GO
IF COL_LENGTH('dbo.TBL_CIP_APP_SETTINGS', 'UPDATED_ON') IS NULL
    ALTER TABLE dbo.TBL_CIP_APP_SETTINGS ADD UPDATED_ON datetime NOT NULL CONSTRAINT DF_CIP_APP_SETTINGS_UPDATED_ON DEFAULT (GETDATE());
GO
IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_APP_SETTINGS WHERE SETTING_KEY = 'Cibil.MonthlyPullLimit')
    INSERT INTO dbo.TBL_CIP_APP_SETTINGS (SETTING_KEY, SETTING_VALUE, UPDATED_BY)
    VALUES ('Cibil.MonthlyPullLimit', '50', NULL);
GO
PRINT 'TBL_CIP_APP_SETTINGS ready.';
