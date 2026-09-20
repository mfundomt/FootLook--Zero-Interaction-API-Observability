-- FootLook account sign-in schema (idempotent).
-- Target: Azure SQL database footlook-accounts.

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Users
    (
        Id                 uniqueidentifier NOT NULL CONSTRAINT PK_Users PRIMARY KEY CONSTRAINT DF_Users_Id DEFAULT NEWID(),
        TenantId           nvarchar(64)     NOT NULL,  -- Entra 'tid' claim (personal MSA tid = 9188040d-6c67-4c5b-b112-36a304b66dad)
        Subject            nvarchar(128)    NOT NULL,  -- Entra 'oid' claim, or 'sub' when oid is absent
        Email              nvarchar(320)    NOT NULL,  -- 'email' claim, falling back to 'preferred_username'
        DisplayName        nvarchar(200)    NOT NULL,
        AccountType        nvarchar(16)     NOT NULL,  -- 'work' or 'personal'
        IsAdmin            bit              NOT NULL CONSTRAINT DF_Users_IsAdmin DEFAULT 0,
        AcceptedTermsAtUtc datetime2(0)     NULL,
        CreatedAtUtc       datetime2(0)     NOT NULL CONSTRAINT DF_Users_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        LastLoginAtUtc     datetime2(0)     NULL,
        LoginCount         int              NOT NULL CONSTRAINT DF_Users_LoginCount DEFAULT 0
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Users_Tenant_Subject' AND object_id = OBJECT_ID(N'dbo.Users'))
    CREATE UNIQUE INDEX UX_Users_Tenant_Subject ON dbo.Users (TenantId, Subject);  -- identity key; never key on email
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Users_Email' AND object_id = OBJECT_ID(N'dbo.Users'))
    CREATE INDEX IX_Users_Email ON dbo.Users (Email);
GO

IF OBJECT_ID(N'dbo.LoginEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoginEvents
    (
        Id            bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_LoginEvents PRIMARY KEY,
        UserId        uniqueidentifier     NOT NULL CONSTRAINT FK_LoginEvents_Users REFERENCES dbo.Users (Id),
        OccurredAtUtc datetime2(0)         NOT NULL CONSTRAINT DF_LoginEvents_OccurredAtUtc DEFAULT SYSUTCDATETIME(),
        Outcome       nvarchar(32)         NOT NULL,  -- 'login' or 'register'
        IpAddress     nvarchar(64)         NULL,
        UserAgent     nvarchar(400)        NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LoginEvents_User_Time' AND object_id = OBJECT_ID(N'dbo.LoginEvents'))
    CREATE INDEX IX_LoginEvents_User_Time ON dbo.LoginEvents (UserId, OccurredAtUtc DESC);
GO
