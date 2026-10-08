/* =============================================================================
   CIP Admin Portal - Database schema
   Target: Microsoft SQL Server 2014+   Database: CIP
   Notes:
     - TBL_CIP_EMPLOYEE_DID is the NEW table requested, with a foreign key
       reference to the existing TBL_CIP_EMPLOYEE_MASTER (EMP_CODE).
     - TBL_CIP_CIBIL_REPORTS is the local cache that lets the CIBIL service
       return an already-pulled PDF for a PAN instead of calling the API again.
============================================================================= */

/* ------------------------- 1. Employee DID (new table) ------------------- */
IF OBJECT_ID('dbo.TBL_CIP_EMPLOYEE_DID', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_EMPLOYEE_DID (
        DID_ID        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CIP_EMPLOYEE_DID PRIMARY KEY,
        EMP_CODE      INT               NULL CONSTRAINT FK_CIP_EMPLOYEE_DID_EMP_MASTER REFERENCES dbo.TBL_CIP_EMPLOYEE_MASTER (EMP_CODE),
        DID_NUMBER    VARCHAR(50)       NOT NULL,
        EXTENSION_NUMBER VARCHAR(20)    NULL,
        DID_STATUS    VARCHAR(20)       NOT NULL CONSTRAINT DF_CIP_EMPLOYEE_DID_STATUS DEFAULT ('Active'),
        ASSIGNED_DATE DATETIME          NOT NULL CONSTRAINT DF_CIP_EMPLOYEE_DID_ASSIGNED DEFAULT (GETDATE()),
        RELEASED_DATE DATETIME          NULL,
        CREATED_BY    INT               NULL,
        CREATED_DATE  DATETIME          NOT NULL CONSTRAINT DF_CIP_EMPLOYEE_DID_CREATED DEFAULT (GETDATE()),
        MODIFIED_BY   INT               NULL,
        MODIFIED_DATE DATETIME          NULL,
        IS_DELETED    INT               NOT NULL CONSTRAINT DF_CIP_EMPLOYEE_DID_ISDEL DEFAULT (0),
        CONSTRAINT UQ_CIP_EMPLOYEE_DID_NUMBER UNIQUE (DID_NUMBER)
    );
END
GO

/* EMP_CODE NULL => generic/shared DID (shows as "OTHER (Generic)" in the UI) */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CIP_EMPLOYEE_DID_EMP' AND object_id = OBJECT_ID('dbo.TBL_CIP_EMPLOYEE_DID'))
    CREATE INDEX IX_CIP_EMPLOYEE_DID_EMP ON dbo.TBL_CIP_EMPLOYEE_DID (EMP_CODE);
GO

/* DID_NUMBER holds the SIP username (e.g. 1001_8652); EXTENSION_NUMBER is the
   last 3 characters of it, kept in sync by the application on assign/change.
   Existing installs of this legacy table only have EXTENSION_NUMBER (the real
   extension), so DID_NUMBER is added in-place and backfilled from it. */
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE name = 'DID_NUMBER' AND object_id = OBJECT_ID('dbo.TBL_CIP_EMPLOYEE_DID'))
    ALTER TABLE dbo.TBL_CIP_EMPLOYEE_DID ADD DID_NUMBER VARCHAR(50) NULL;
GO

/* CloudConnect dialer config, merged into the DID row: every DID carries the
   CRM API + SIP registration credentials used for click2call. Nulls fall back
   to these defaults in the application layer. */
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE name = 'BASE_URL'           AND object_id = OBJECT_ID('dbo.TBL_CIP_EMPLOYEE_DID'))
    ALTER TABLE dbo.TBL_CIP_EMPLOYEE_DID ADD
        BASE_URL           VARCHAR(300) NULL,
        CLICK2CALL_PATH    VARCHAR(200) NULL,
        TOKEN_ID           VARCHAR(200) NULL,
        EXTENSION_PASSWORD VARCHAR(300) NULL,
        SIP_SERVER         VARCHAR(200) NULL,
        SIP_PORT           INT          NULL,
        SIP_PASSWORD       VARCHAR(300) NULL,
        SIP_REALM          VARCHAR(200) NULL;
GO

/* Seed the shipped CloudConnect config into rows that have none yet. */
UPDATE dbo.TBL_CIP_EMPLOYEE_DID
   SET BASE_URL           = 'https://crm2.cloud-connect.in/ccpl_api/v1.4/',
       CLICK2CALL_PATH    = 'api/info/click2call',
       TOKEN_ID           = '6JU4KMMIO6B1xzLt',
       EXTENSION_PASSWORD = '15e8e7c72ea16ca290930fd7c4db760b',
       SIP_SERVER         = 'sip2.cloud-connect.in',
       SIP_PORT           = 7065,
       SIP_PASSWORD       = 'Tejas@123',
       SIP_REALM          = 'sip2.cloud-connect.in'
 WHERE SIP_SERVER IS NULL;
GO

UPDATE dbo.TBL_CIP_EMPLOYEE_DID
   SET DID_NUMBER = EXTENSION_NUMBER
 WHERE (DID_NUMBER IS NULL OR DID_NUMBER = '') AND ISNULL(EXTENSION_NUMBER, '') <> '';
GO

/* ------------------------- 2. DID change history -------------------------- */
IF OBJECT_ID('dbo.TBL_CIP_EMPLOYEE_DID_HISTORY', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_EMPLOYEE_DID_HISTORY (
        HISTORY_ID     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CIP_EMPLOYEE_DID_HIST PRIMARY KEY,
        EMP_CODE       INT               NOT NULL CONSTRAINT FK_CIP_DID_HIST_EMP REFERENCES dbo.TBL_CIP_EMPLOYEE_MASTER (EMP_CODE),
        OLD_DID_NUMBER VARCHAR(20)       NULL,
        NEW_DID_NUMBER VARCHAR(20)       NULL,
        CHANGED_BY     INT               NULL,
        CHANGED_DATE   DATETIME          NOT NULL CONSTRAINT DF_CIP_DID_HIST_DATE DEFAULT (GETDATE())
    );
END
GO

/* ------------------------- 3. CIBIL report cache -------------------------- */
IF OBJECT_ID('dbo.TBL_CIP_CIBIL_REPORTS', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_CIBIL_REPORTS (
        REPORT_ID   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CIP_CIBIL_REPORTS PRIMARY KEY,
        PAN_NO      VARCHAR(10)       NOT NULL,
        APPLICANT_NAME VARCHAR(150)   NULL,
        MOBILE      VARCHAR(15)       NULL,
        GENDER      VARCHAR(10)       NULL,
        REPORT_TYPE VARCHAR(20)       NOT NULL CONSTRAINT DF_CIP_CIBIL_TYPE DEFAULT ('cibil'),
        FILE_NAME   VARCHAR(200)      NOT NULL,
        FILE_PATH   VARCHAR(400)      NOT NULL,
        FILE_SIZE   BIGINT            NULL,
        PULLED_BY   INT               NULL,
        PULLED_BY_NAME VARCHAR(100)   NULL,
        PULLED_DATE DATETIME          NOT NULL CONSTRAINT DF_CIP_CIBIL_DATE DEFAULT (GETDATE()),
        SOURCE      VARCHAR(10)       NOT NULL CONSTRAINT DF_CIP_CIBIL_SOURCE DEFAULT ('API')   -- API | CACHE | REGEN
    );
    -- One report per PAN per report-type per calendar month (rule: a PAN can be
    -- pulled once a month by any user; admins regenerate by replacing that month's row).
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_CIP_CIBIL_PAN_TYPE_MONTH' AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_REPORTS'))
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE name = 'PULL_MONTH' AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_REPORTS'))
            EXEC('ALTER TABLE dbo.TBL_CIP_CIBIL_REPORTS ADD PULL_MONTH AS CONVERT(VARCHAR(7), PULLED_DATE, 126) PERSISTED');
        CREATE UNIQUE INDEX UQ_CIP_CIBIL_PAN_TYPE_MONTH ON dbo.TBL_CIP_CIBIL_REPORTS (PAN_NO, REPORT_TYPE, PULL_MONTH);
    END
END
GO

/* --------------- 3b. CIBIL monthly pull-limit requests (user -> admin) ---- */
IF OBJECT_ID('dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS (
        REQUEST_ID     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CIP_CIBIL_LIMIT_REQ PRIMARY KEY,
        EMP_CODE       INT               NOT NULL,
        EMP_NAME       VARCHAR(200)      NOT NULL,
        REQUEST_MONTH  VARCHAR(7)        NOT NULL CONSTRAINT DF_CIP_LIMIT_MONTH DEFAULT (CONVERT(VARCHAR(7), GETDATE(), 126)),
        CURRENT_COUNT  INT               NOT NULL CONSTRAINT DF_CIP_LIMIT_CNT DEFAULT (0),
        REQUESTED_LIMIT INT              NOT NULL,
        REASON         VARCHAR(500)      NULL,
        STATUS         VARCHAR(20)       NOT NULL CONSTRAINT DF_CIP_LIMIT_STATUS DEFAULT ('Pending'),  -- Pending | Approved | Rejected
        APPROVED_LIMIT INT               NULL,
        DECIDED_BY     INT               NULL,
        DECIDED_BY_NAME VARCHAR(200)     NULL,
        DECIDED_DATE   DATETIME          NULL,
        CREATED_DATE   DATETIME          NOT NULL CONSTRAINT DF_CIP_LIMIT_CREATED DEFAULT (GETDATE())
    );
    CREATE UNIQUE INDEX UQ_CIP_LIMIT_REQ ON dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS (EMP_CODE, REQUEST_MONTH, STATUS) WHERE STATUS = 'Pending';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CIP_CIBIL_DATE' AND object_id = OBJECT_ID('dbo.TBL_CIP_CIBIL_REPORTS'))
    CREATE INDEX IX_CIP_CIBIL_DATE ON dbo.TBL_CIP_CIBIL_REPORTS (PULLED_DATE);
GO

/* ------------------------- 4. Login logs ---------------------------------- */
IF OBJECT_ID('dbo.TBL_CIP_LOGIN_LOGS', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_LOGIN_LOGS (
        LOG_ID     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CIP_LOGIN_LOGS PRIMARY KEY,
        EMP_CODE   INT               NULL,
        USERNAME   VARCHAR(100)      NULL,
        LOGIN_TIME DATETIME          NOT NULL CONSTRAINT DF_CIP_LOGIN_TIME DEFAULT (GETDATE()),
        LOGOUT_TIME DATETIME         NULL,
        IP_ADDRESS VARCHAR(50)       NULL,
        HOST_NAME  VARCHAR(200)      NULL,
        STATUS     VARCHAR(30)       NULL,   -- SUCCESS | FAILED | LOGOUT
        TOKEN_JTI  VARCHAR(64)       NULL
    );
END
GO

/* ------------------------- 5. Demo seed data ------------------------------
   DIDs +91 98765 43210..43214 are assigned to the first five on-roll,
   non-deleted, non-CONNECTOR employees; 43215 stays as the generic/shared row.
   Adjust or delete freely.                                                  */
IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_EMPLOYEE_DID WHERE DID_NUMBER = '+919876543215')
    INSERT INTO dbo.TBL_CIP_EMPLOYEE_DID (EMP_CODE, DID_NUMBER, DID_STATUS, CREATED_BY)
    VALUES (NULL, '+919876543215', 'Active', 10);
GO

;WITH first5 AS (
    SELECT TOP 5 EMP_CODE,
           ROW_NUMBER() OVER (ORDER BY EMP_CODE) AS rn
    FROM dbo.TBL_CIP_EMPLOYEE_MASTER
    WHERE LAST_WORKING_DATE IS NULL
      AND ISNULL(IS_DELETED, 0) = 0
      AND ISNULL(ROLE_ID, 0) <> 2010
      AND ISNULL(DESIG_ID, 0) <> 1014
    ORDER BY EMP_CODE
)
INSERT INTO dbo.TBL_CIP_EMPLOYEE_DID (EMP_CODE, DID_NUMBER, DID_STATUS, CREATED_BY)
SELECT f.EMP_CODE, '+91987654321' + CAST(f.rn - 1 AS VARCHAR(2)), 'Active', 10
FROM first5 f
WHERE NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_EMPLOYEE_DID d WHERE d.EMP_CODE = f.EMP_CODE AND d.IS_DELETED = 0);
GO

PRINT 'CIP Admin schema deployed successfully.';
GO

-- ============================================================
-- Menu Master: sidebar menus per role
-- ============================================================
IF OBJECT_ID('dbo.TBL_CIP_MENU_MASTER', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_MENU_MASTER (
        MENU_ID     int IDENTITY(1,1) PRIMARY KEY,
        MENU_LABEL  varchar(100) NOT NULL,
        MENU_PATH   varchar(200) NULL,
        PARENT_ID   int NULL REFERENCES dbo.TBL_CIP_MENU_MASTER(MENU_ID),
        ICON        varchar(50) NULL,
        SORT_ORDER  int NOT NULL DEFAULT 0,
        IS_ACTIVE   bit NOT NULL DEFAULT 1
    );
END
GO
IF OBJECT_ID('dbo.TBL_CIP_ROLE_MENU', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_ROLE_MENU (
        ROLE_ID int NOT NULL,
        MENU_ID int NOT NULL REFERENCES dbo.TBL_CIP_MENU_MASTER(MENU_ID),
        CONSTRAINT PK_TBL_CIP_ROLE_MENU PRIMARY KEY (ROLE_ID, MENU_ID)
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER)
BEGIN
    DECLARE @Masters int, @Reports int, @Admin int;
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, ICON, SORT_ORDER) VALUES ('Dashboard','/','dashboard',1);
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, ICON, SORT_ORDER) VALUES ('CIBIL Checker','/cibil','search',2);
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, ICON, SORT_ORDER) VALUES ('Masters',NULL,'database',3);
    SET @Masters = SCOPE_IDENTITY();
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('Employee Master','/employee-master',@Masters,1);
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('Employees DID','/employees',@Masters,2);
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('Company Category','/company-categories',@Masters,3);
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, ICON, SORT_ORDER) VALUES ('Reports',NULL,'report',4);
    SET @Reports = SCOPE_IDENTITY();
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('Dialer Live','/dialer',@Reports,1);
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('Generic Reports','/generic-reports',@Reports,2);
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('CIBIL Reports','/reports',@Reports,3);
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, ICON, SORT_ORDER) VALUES ('Administration',NULL,'shield',5);
    SET @Admin = SCOPE_IDENTITY();
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('CIBIL Limit Requests','/limit-requests',@Admin,1);
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('Menu Master','/menu-master',@Admin,2);
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, ICON, SORT_ORDER) VALUES ('Settings','/settings','settings',6);

    -- Every active role gets everything except Administration (roles 1 & 2 only).
    INSERT dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
    SELECT r.ROLE_ID, m.MENU_ID FROM dbo.TBL_CIP_ROLE_MASTER r CROSS JOIN dbo.TBL_CIP_MENU_MASTER m
    WHERE ISNULL(r.ROLE_ACTIVE, 0) = 1
      AND m.MENU_ID NOT IN (SELECT MENU_ID FROM dbo.TBL_CIP_MENU_MASTER WHERE PARENT_ID = @Admin)
      AND m.MENU_ID <> @Admin
    UNION
    SELECT r.ROLE_ID, m.MENU_ID FROM dbo.TBL_CIP_ROLE_MASTER r CROSS JOIN dbo.TBL_CIP_MENU_MASTER m
    WHERE r.ROLE_ID IN (1, 2) AND (m.MENU_ID = @Admin OR m.PARENT_ID = @Admin);
END
GO

PRINT 'Menu Master schema deployed successfully.';
GO

-- ============================================================
-- Employee documents (PAN, Aadhaar, offer letters, ...)
-- Files live under Backend/employee documents/<EMP_CODE>/.
-- ============================================================
IF OBJECT_ID('dbo.TBL_CIP_EMPLOYEE_DOCUMENTS', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_EMPLOYEE_DOCUMENTS (
        DOC_ID           int IDENTITY(1,1) PRIMARY KEY,
        EMP_CODE         int NOT NULL,
        DOC_TYPE         varchar(30) NOT NULL,          -- PAN / Aadhaar / Offer Letter / ...
        FILE_NAME        nvarchar(260) NOT NULL,        -- original file name
        FILE_PATH        nvarchar(500) NOT NULL,        -- relative to the backend base directory
        FILE_SIZE        int NOT NULL DEFAULT 0,
        CONTENT_TYPE     varchar(100) NULL,
        SOURCE           varchar(20) NOT NULL DEFAULT 'UPLOAD',  -- UPLOAD | OFFER (system generated)
        UPLOADED_BY      int NULL,
        UPLOADED_BY_NAME varchar(100) NULL,
        CREATED_DATE     datetime NOT NULL DEFAULT GETDATE(),
        IS_DELETED       bit NOT NULL DEFAULT 0
    );
    CREATE INDEX IX_EMPDOC_EMP ON dbo.TBL_CIP_EMPLOYEE_DOCUMENTS(EMP_CODE, IS_DELETED);
END
GO

PRINT 'Employee documents schema deployed successfully.';
GO

-- ============================================================
-- Lead Master — sales/tele-calling leads tracked in the admin portal.
-- ============================================================
IF OBJECT_ID('dbo.TBL_CIP_LEAD_MASTER', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_LEAD_MASTER (
        LEAD_ID          int IDENTITY(1,1) PRIMARY KEY,
        LEAD_NAME        nvarchar(150) NOT NULL,
        MOBILE           varchar(15)   NULL,
        EMAIL            nvarchar(150) NULL,
        LOCATION         nvarchar(100) NULL,
        SOURCE           varchar(50)   NULL,             -- Walk-in / Website / Reference / ... 
        REMARKS          nvarchar(500) NULL,
        ASSIGNED_TO      int           NULL REFERENCES dbo.TBL_CIP_EMPLOYEE_MASTER (EMP_CODE),
        LEAD_STATUS      varchar(20)   NOT NULL CONSTRAINT DF_CIP_LEAD_STATUS DEFAULT ('New'),  -- New | In Progress | Converted | Lost
        IS_ACTIVE        bit           NOT NULL CONSTRAINT DF_CIP_LEAD_ACTIVE DEFAULT (1),
        CREATED_BY       int           NULL,
        CREATED_BY_NAME  nvarchar(100) NULL,
        CREATED_DATE     datetime      NOT NULL CONSTRAINT DF_CIP_LEAD_CREATED DEFAULT (GETDATE()),
        MODIFIED_BY      int           NULL,
        MODIFIED_DATE    datetime      NULL,
        IS_DELETED       bit           NOT NULL CONSTRAINT DF_CIP_LEAD_ISDEL DEFAULT (0)
    );
    CREATE INDEX IX_CIP_LEAD_STATUS ON dbo.TBL_CIP_LEAD_MASTER (LEAD_STATUS, IS_ACTIVE, IS_DELETED);
    CREATE INDEX IX_CIP_LEAD_ASSIGNED ON dbo.TBL_CIP_LEAD_MASTER (ASSIGNED_TO);
END
GO

PRINT 'Lead Master schema deployed successfully.';
GO

-- ============================================================
-- Menus for the new modules (idempotent — safe to re-run).
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_PATH = '/lead-master')
BEGIN
    DECLARE @Masters2 int;
    SELECT @Masters2 = MENU_ID FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_PATH IS NULL AND MENU_LABEL = 'Masters';
    IF @Masters2 IS NULL
    BEGIN
        INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, ICON, SORT_ORDER) VALUES ('Masters', NULL, 'database', 3);
        SET @Masters2 = SCOPE_IDENTITY();
    END
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('Lead Master', '/lead-master', @Masters2, 4);
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_PATH = '/role-master')
BEGIN
    DECLARE @Admin2 int;
    SELECT @Admin2 = MENU_ID FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_PATH IS NULL AND MENU_LABEL = 'Administration';
    IF @Admin2 IS NULL
    BEGIN
        INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, ICON, SORT_ORDER) VALUES ('Administration', NULL, 'shield', 5);
        SET @Admin2 = SCOPE_IDENTITY();
    END
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('Role & Privilege Master', '/role-master', @Admin2, 3);
END
GO

-- Grant the new menus the same way the original seed did:
-- every active role gets non-Administration menus; roles 1 & 2 also get Administration.
INSERT dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
SELECT r.ROLE_ID, m.MENU_ID
FROM dbo.TBL_CIP_ROLE_MASTER r
CROSS JOIN dbo.TBL_CIP_MENU_MASTER m
WHERE m.MENU_PATH IN ('/lead-master', '/role-master')
  AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU x WHERE x.ROLE_ID = r.ROLE_ID AND x.MENU_ID = m.MENU_ID)
  AND ( (m.MENU_PATH = '/lead-master' AND ISNULL(r.ROLE_ACTIVE, 0) = 1)
     OR (m.MENU_PATH = '/role-master' AND r.ROLE_ID IN (1, 2)) );
GO

PRINT 'Lead Master + Role Master menus deployed successfully.';
GO

-- ============================================================
-- OCR to Excel — uploads of spreadsheet photos/screenshots, converted
-- to a grid, previewed/edited in the portal and exported to .xlsx.
-- ============================================================
IF OBJECT_ID('dbo.TBL_CIP_OCR_JOBS', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TBL_CIP_OCR_JOBS (
        JOB_ID           int IDENTITY(1,1) PRIMARY KEY,
        EMP_CODE         int            NOT NULL,
        ORIGINAL_FILE_NAME nvarchar(260) NOT NULL,
        STORED_FILE_NAME nvarchar(260)  NOT NULL,
        FILE_PATH        nvarchar(500)  NOT NULL,     -- relative to the backend base directory
        FILE_SIZE        int            NOT NULL DEFAULT 0,
        CONTENT_TYPE     varchar(100)   NULL,
        ROW_COUNT        int            NOT NULL DEFAULT 0,
        COL_COUNT        int            NOT NULL DEFAULT 0,
        CREATED_DATE     datetime       NOT NULL DEFAULT GETDATE(),
        IS_DELETED       bit            NOT NULL DEFAULT 0
    );
    CREATE INDEX IX_CIP_OCR_JOBS_EMP ON dbo.TBL_CIP_OCR_JOBS (EMP_CODE, IS_DELETED, CREATED_DATE);
END
GO

PRINT 'OCR jobs schema deployed successfully.';
GO

IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_PATH = '/ocr')
BEGIN
    DECLARE @Masters3 int;
    SELECT @Masters3 = MENU_ID FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_PATH IS NULL AND MENU_LABEL = 'Masters';
    IF @Masters3 IS NULL
    BEGIN
        INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, ICON, SORT_ORDER) VALUES ('Masters', NULL, 'database', 3);
        SET @Masters3 = SCOPE_IDENTITY();
    END
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('OCR to Excel', '/ocr', @Masters3, 5);
END
GO

INSERT dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
SELECT r.ROLE_ID, m.MENU_ID
FROM dbo.TBL_CIP_ROLE_MASTER r
CROSS JOIN dbo.TBL_CIP_MENU_MASTER m
WHERE m.MENU_PATH = '/ocr'
  AND ISNULL(r.ROLE_ACTIVE, 0) = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU x WHERE x.ROLE_ID = r.ROLE_ID AND x.MENU_ID = m.MENU_ID);
GO

PRINT 'OCR to Excel menu deployed successfully.';
GO

-- ============================================================
-- BSA Analyzer (Bank Statement Analyzer / CIBIL analyzer module)
-- Stateless analysis — no tables needed; just a sidebar menu entry
-- under Masters so roles can reach it.
-- ============================================================
DECLARE @Masters4 int;
SELECT @Masters4 = MENU_ID FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_LABEL = 'Masters' AND MENU_PATH IS NULL;

IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_PATH = '/bsa-analyzer')
BEGIN
    IF @Masters4 IS NULL
    BEGIN
        INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, ICON, SORT_ORDER) VALUES ('Masters', NULL, 'database', 3);
        SET @Masters4 = SCOPE_IDENTITY();
    END
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('BSA Analyzer', '/bsa-analyzer', @Masters4, 6);
END
GO

INSERT dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
SELECT r.ROLE_ID, m.MENU_ID
FROM dbo.TBL_CIP_ROLE_MASTER r
CROSS JOIN dbo.TBL_CIP_MENU_MASTER m
WHERE m.MENU_PATH = '/bsa-analyzer'
  AND ISNULL(r.ROLE_ACTIVE, 0) = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU x WHERE x.ROLE_ID = r.ROLE_ID AND x.MENU_ID = m.MENU_ID);
GO

PRINT 'BSA Analyzer menu deployed successfully.';
GO

-- ============================================================
-- Menu restructure: CIBIL group (Checker + Analyzer), all module
-- menus guaranteed present. Idempotent — safe to re-run.
-- ============================================================
DECLARE @CibilGroup int;
SELECT @CibilGroup = MENU_ID FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_LABEL = 'CIBIL' AND MENU_PATH IS NULL;

IF @CibilGroup IS NULL
BEGIN
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, ICON, SORT_ORDER) VALUES ('CIBIL', NULL, 'search', 2);
    SET @CibilGroup = SCOPE_IDENTITY();
END

-- Move the existing top-level CIBIL Checker under the new CIBIL group.
UPDATE dbo.TBL_CIP_MENU_MASTER
   SET PARENT_ID = @CibilGroup, MENU_LABEL = 'Checker', ICON = NULL
 WHERE MENU_PATH = '/cibil' AND ISNULL(PARENT_ID, 0) <> @CibilGroup;

-- BSA Analyzer lives under CIBIL as 'Analyzer' (moved out of Masters if it was there).
IF NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_MENU_MASTER WHERE MENU_PATH = '/bsa-analyzer')
    INSERT dbo.TBL_CIP_MENU_MASTER (MENU_LABEL, MENU_PATH, PARENT_ID, SORT_ORDER) VALUES ('Analyzer', '/bsa-analyzer', @CibilGroup, 2);
ELSE
    UPDATE dbo.TBL_CIP_MENU_MASTER
       SET PARENT_ID = @CibilGroup, MENU_LABEL = 'Analyzer'
     WHERE MENU_PATH = '/bsa-analyzer' AND ISNULL(PARENT_ID, 0) <> @CibilGroup;
GO

-- Grant every menu that lacks any role grant to all active roles
-- (covers CIBIL Checker, Analyzer, OCR, Lead Master, Role Master, etc.).
INSERT dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID)
SELECT r.ROLE_ID, m.MENU_ID
FROM dbo.TBL_CIP_ROLE_MASTER r
CROSS JOIN dbo.TBL_CIP_MENU_MASTER m
WHERE ISNULL(r.ROLE_ACTIVE, 0) = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_ROLE_MENU x WHERE x.ROLE_ID = r.ROLE_ID AND x.MENU_ID = m.MENU_ID);
GO

PRINT 'Menu restructure (CIBIL group + module menus) deployed successfully.';
GO
