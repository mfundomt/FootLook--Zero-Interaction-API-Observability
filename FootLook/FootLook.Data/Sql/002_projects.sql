-- FootLook central service schema: projects, members, invite codes (idempotent, additive only).
-- Target: Azure SQL database footlook-accounts, after 001_create_users.sql.
-- Never alters dbo.Users or dbo.LoginEvents; it only references dbo.Users(Id).

IF OBJECT_ID(N'dbo.Projects', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Projects
    (
        Id           nvarchar(32)     NOT NULL CONSTRAINT PK_Projects PRIMARY KEY,  -- 'prj_' + 16 random base32 chars; public, unguessable
        Name         nvarchar(100)    NOT NULL,
        OwnerUserId  uniqueidentifier NOT NULL CONSTRAINT FK_Projects_Users REFERENCES dbo.Users (Id),
        CreatedAtUtc datetime2(0)     NOT NULL CONSTRAINT DF_Projects_CreatedAtUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Projects_Owner' AND object_id = OBJECT_ID(N'dbo.Projects'))
    CREATE INDEX IX_Projects_Owner ON dbo.Projects (OwnerUserId);
GO

IF OBJECT_ID(N'dbo.ProjectReturnUrls', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectReturnUrls
    (
        ProjectId nvarchar(32)  NOT NULL CONSTRAINT FK_ProjectReturnUrls_Projects REFERENCES dbo.Projects (Id) ON DELETE CASCADE,
        Url       nvarchar(300) NOT NULL,  -- normalised absolute URL the dashboard may be sent back to
        CONSTRAINT PK_ProjectReturnUrls PRIMARY KEY (ProjectId, Url)
    );
END
GO

IF OBJECT_ID(N'dbo.ProjectMembers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectMembers
    (
        ProjectId    nvarchar(32)     NOT NULL CONSTRAINT FK_ProjectMembers_Projects REFERENCES dbo.Projects (Id) ON DELETE CASCADE,
        UserId       uniqueidentifier NOT NULL CONSTRAINT FK_ProjectMembers_Users REFERENCES dbo.Users (Id),
        Role         nvarchar(16)     NOT NULL CONSTRAINT CK_ProjectMembers_Role CHECK (Role IN (N'owner', N'member')),
        AddedAtUtc   datetime2(0)     NOT NULL CONSTRAINT DF_ProjectMembers_AddedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ProjectMembers PRIMARY KEY (ProjectId, UserId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectMembers_User' AND object_id = OBJECT_ID(N'dbo.ProjectMembers'))
    CREATE INDEX IX_ProjectMembers_User ON dbo.ProjectMembers (UserId);
GO

IF OBJECT_ID(N'dbo.ProjectInvites', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectInvites
    (
        Id              bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProjectInvites PRIMARY KEY,
        ProjectId       nvarchar(32)     NOT NULL CONSTRAINT FK_ProjectInvites_Projects REFERENCES dbo.Projects (Id) ON DELETE CASCADE,
        CodeHash        char(64)         NOT NULL CONSTRAINT UQ_ProjectInvites_CodeHash UNIQUE,  -- lowercase hex SHA-256 of the invite code; the code itself is never stored
        CreatedByUserId uniqueidentifier NOT NULL,
        CreatedAtUtc    datetime2(0)     NOT NULL CONSTRAINT DF_ProjectInvites_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        ExpiresAtUtc    datetime2(0)     NOT NULL,
        MaxUses         int              NOT NULL,
        UsedCount       int              NOT NULL CONSTRAINT DF_ProjectInvites_UsedCount DEFAULT 0,
        RevokedAtUtc    datetime2(0)     NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectInvites_Project' AND object_id = OBJECT_ID(N'dbo.ProjectInvites'))
    CREATE INDEX IX_ProjectInvites_Project ON dbo.ProjectInvites (ProjectId);
GO
