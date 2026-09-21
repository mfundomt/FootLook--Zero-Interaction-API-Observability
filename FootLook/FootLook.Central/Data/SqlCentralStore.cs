using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FootLook.Central.Data;

/// <summary>Project ids: "prj_" + 16 random lower-case base32 characters (80 bits): public, unguessable, url-safe.</summary>
public static class ProjectIds
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
    private const int BodyLength = 16;

    public static string New()
    {
        var body = new char[BodyLength];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return "prj_" + new string(body);
    }

    public static bool IsWellFormed(string? id)
    {
        if (id is null || id.Length != 4 + BodyLength || !id.StartsWith("prj_", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in id.AsSpan(4))
        {
            if (Alphabet.IndexOf(c) < 0)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Projects, members and invites in Azure SQL (schema: FootLook.Data/Sql/002_projects.sql). Plain ADO.NET,
/// parameterised SQL only, and the same transient-failure retry as FootLook.Data's SqlFootLookAccountStore.
/// <para>
/// Every multi-step change is one transaction. Limits (projects per owner, live invites per project, members
/// per project) are checked under a lock that serialises the writers that could break them, so two
/// simultaneous requests cannot both slip past a limit. Redeeming an invite locks the invite row and the
/// project row, re-checks membership and capacity, and consumes a use with an UPDATE guarded by
/// UsedCount &lt; MaxUses, so a code with N uses can never admit more than N people.
/// </para>
/// <para>Only the SHA-256 hash of an invite code reaches this class.</para>
/// </summary>
public sealed class SqlCentralStore : ICentralStore
{
    private const int MaxTransientAttempts = 6;
    private const int MaxDuplicateKeyRetries = 3;

    private static readonly int[] DuplicateKeyErrors = { 2601, 2627 };
    private const int ForeignKeyViolation = 547;

    // Errors that mean "try again": database resuming/unavailable, throttling, failover,
    // deadlock victim, timeouts and connection drops.
    private static readonly int[] TransientErrors =
    {
        40613, 40197, 40501, 49918, 49919, 49920, 4060, 10928, 10929, 1205, -2, 64, 10053, 10054, 10060, 233,
    };

    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20),
    };

    private readonly string _connectionString;
    private readonly ILogger? _logger;
    private readonly TimeSpan[] _retryDelays;

    public SqlCentralStore(string connectionString, ILogger<SqlCentralStore>? logger = null)
        : this(connectionString, logger, RetryDelays)
    {
    }

    internal SqlCentralStore(string connectionString, ILogger? logger, TimeSpan[] retryDelays)
    {
        _connectionString = connectionString;
        _logger = logger;
        _retryDelays = retryDelays;
    }

    // ---- projects -------------------------------------------------------------------------------

    public async Task<CreateProjectResult> CreateProjectAsync(
        Guid ownerId, string name, IReadOnlyList<string> returnUrls, int maxOwnedProjects, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var projectId = ProjectIds.New();
            try
            {
                return await WithConnectionAsync(
                    (connection, ct) => CreateProjectOnceAsync(connection, projectId, ownerId, name, returnUrls, maxOwnedProjects, ct),
                    cancellationToken);
            }
            catch (SqlException ex) when (IsDuplicateKey(ex) && attempt <= MaxDuplicateKeyRetries)
            {
                // An id collision (2^-80): draw another.
            }
        }
    }

    private static async Task<CreateProjectResult> CreateProjectOnceAsync(
        SqlConnection connection, string projectId, Guid ownerId, string name, IReadOnlyList<string> returnUrls, int maxOwnedProjects, CancellationToken ct)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // One creator per owner at a time, so the count below cannot be raced past the limit.
        await AcquireLockAsync(connection, transaction, "footlook:project-create:" + ownerId.ToString("N"), ct);

        await using (var count = new SqlCommand("SELECT COUNT(*) FROM dbo.Projects WHERE OwnerUserId = @owner", connection, transaction))
        {
            count.Parameters.Add(Param("@owner", SqlDbType.UniqueIdentifier, 0, ownerId));
            if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) >= maxOwnedProjects)
            {
                return new CreateProjectResult(CreateProjectStatus.LimitReached, null);
            }
        }

        DateTime createdAtUtc;
        try
        {
            await using (var insert = new SqlCommand(
                "INSERT INTO dbo.Projects (Id, Name, OwnerUserId) OUTPUT inserted.CreatedAtUtc VALUES (@id, @name, @owner)",
                connection, transaction))
            {
                insert.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
                insert.Parameters.Add(Param("@name", SqlDbType.NVarChar, 100, name));
                insert.Parameters.Add(Param("@owner", SqlDbType.UniqueIdentifier, 0, ownerId));
                createdAtUtc = AsUtc((DateTime)(await insert.ExecuteScalarAsync(ct))!);
            }
        }
        catch (SqlException ex) when (ex.Number == ForeignKeyViolation)
        {
            // The token names a user that no longer exists.
            return new CreateProjectResult(CreateProjectStatus.UnknownUser, null);
        }

        await using (var member = new SqlCommand(
            "INSERT INTO dbo.ProjectMembers (ProjectId, UserId, Role) VALUES (@id, @user, N'owner')", connection, transaction))
        {
            member.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
            member.Parameters.Add(Param("@user", SqlDbType.UniqueIdentifier, 0, ownerId));
            await member.ExecuteNonQueryAsync(ct);
        }

        await InsertReturnUrlsAsync(connection, transaction, projectId, returnUrls, ct);
        await transaction.CommitAsync(ct);

        return new CreateProjectResult(
            CreateProjectStatus.Created,
            new ProjectDetail(projectId, name, ProjectRoles.Owner, returnUrls.ToList(), createdAtUtc, 1));
    }

    public Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        WithConnectionAsync<IReadOnlyList<ProjectSummary>>(async (connection, ct) =>
        {
            await using var command = new SqlCommand(
                "SELECT p.Id, p.Name, m.Role, p.CreatedAtUtc, " +
                "       (SELECT COUNT(*) FROM dbo.ProjectMembers c WHERE c.ProjectId = p.Id) " +
                "FROM dbo.ProjectMembers m JOIN dbo.Projects p ON p.Id = m.ProjectId " +
                "WHERE m.UserId = @user ORDER BY p.CreatedAtUtc DESC, p.Id",
                connection);
            command.Parameters.Add(Param("@user", SqlDbType.UniqueIdentifier, 0, userId));

            var list = new List<ProjectSummary>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new ProjectSummary(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(4), AsUtc(reader.GetDateTime(3))));
            }

            return list;
        }, cancellationToken);

    public Task<string?> GetRoleAsync(string projectId, Guid userId, CancellationToken cancellationToken = default) =>
        WithConnectionAsync(async (connection, ct) =>
        {
            await using var command = new SqlCommand(
                "SELECT Role FROM dbo.ProjectMembers WHERE ProjectId = @id AND UserId = @user", connection);
            command.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
            command.Parameters.Add(Param("@user", SqlDbType.UniqueIdentifier, 0, userId));
            return (string?)await command.ExecuteScalarAsync(ct);
        }, cancellationToken);

    public Task<ProjectDetail?> GetProjectAsync(string projectId, Guid userId, CancellationToken cancellationToken = default) =>
        WithConnectionAsync((connection, ct) => ReadDetailAsync(connection, null, projectId, userId, ct), cancellationToken);

    public async Task<ProjectDetail?> UpdateProjectAsync(
        string projectId, Guid ownerId, string? name, IReadOnlyList<string>? returnUrls, CancellationToken cancellationToken = default)
    {
        return await WithConnectionAsync(async (connection, ct) =>
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            await using (var update = new SqlCommand(
                // The owner condition is the authorisation check; a zero row count means "not yours".
                "UPDATE dbo.Projects SET Name = COALESCE(@name, Name) WHERE Id = @id AND OwnerUserId = @owner", connection, transaction))
            {
                update.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
                update.Parameters.Add(Param("@owner", SqlDbType.UniqueIdentifier, 0, ownerId));
                update.Parameters.Add(Param("@name", SqlDbType.NVarChar, 100, name));
                if (await update.ExecuteNonQueryAsync(ct) == 0)
                {
                    return null;
                }
            }

            if (returnUrls is not null)
            {
                await using (var delete = new SqlCommand("DELETE FROM dbo.ProjectReturnUrls WHERE ProjectId = @id", connection, transaction))
                {
                    delete.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
                    await delete.ExecuteNonQueryAsync(ct);
                }

                await InsertReturnUrlsAsync(connection, transaction, projectId, returnUrls, ct);
            }

            var detail = await ReadDetailAsync(connection, transaction, projectId, ownerId, ct);
            await transaction.CommitAsync(ct);
            return detail;
        }, cancellationToken);
    }

    public Task<bool> DeleteProjectAsync(string projectId, Guid ownerId, CancellationToken cancellationToken = default) =>
        WithConnectionAsync(async (connection, ct) =>
        {
            // ON DELETE CASCADE removes the members, invites and return URLs.
            await using var command = new SqlCommand("DELETE FROM dbo.Projects WHERE Id = @id AND OwnerUserId = @owner", connection);
            command.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
            command.Parameters.Add(Param("@owner", SqlDbType.UniqueIdentifier, 0, ownerId));
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }, cancellationToken);

    private static async Task<ProjectDetail?> ReadDetailAsync(SqlConnection connection, SqlTransaction? transaction, string projectId, Guid userId, CancellationToken ct)
    {
        string name, role;
        DateTime createdAtUtc;
        int memberCount;

        await using (var command = new SqlCommand(
            "SELECT p.Name, m.Role, p.CreatedAtUtc, (SELECT COUNT(*) FROM dbo.ProjectMembers c WHERE c.ProjectId = p.Id) " +
            "FROM dbo.Projects p JOIN dbo.ProjectMembers m ON m.ProjectId = p.Id AND m.UserId = @user WHERE p.Id = @id",
            connection, transaction))
        {
            command.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
            command.Parameters.Add(Param("@user", SqlDbType.UniqueIdentifier, 0, userId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            name = reader.GetString(0);
            role = reader.GetString(1);
            createdAtUtc = AsUtc(reader.GetDateTime(2));
            memberCount = reader.GetInt32(3);
        }

        var urls = new List<string>();
        await using (var command = new SqlCommand(
            "SELECT Url FROM dbo.ProjectReturnUrls WHERE ProjectId = @id ORDER BY Url", connection, transaction))
        {
            command.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                urls.Add(reader.GetString(0));
            }
        }

        return new ProjectDetail(projectId, name, role, urls, createdAtUtc, memberCount);
    }

    private static async Task InsertReturnUrlsAsync(SqlConnection connection, SqlTransaction transaction, string projectId, IReadOnlyList<string> urls, CancellationToken ct)
    {
        foreach (var url in urls)
        {
            await using var command = new SqlCommand("INSERT INTO dbo.ProjectReturnUrls (ProjectId, Url) VALUES (@id, @url)", connection, transaction);
            command.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
            command.Parameters.Add(Param("@url", SqlDbType.NVarChar, 300, url));
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    // ---- members --------------------------------------------------------------------------------

    public Task<IReadOnlyList<ProjectMember>> ListMembersAsync(string projectId, CancellationToken cancellationToken = default) =>
        WithConnectionAsync<IReadOnlyList<ProjectMember>>(async (connection, ct) =>
        {
            await using var command = new SqlCommand(
                "SELECT u.Id, u.Email, u.DisplayName, m.Role, m.AddedAtUtc " +
                "FROM dbo.ProjectMembers m JOIN dbo.Users u ON u.Id = m.UserId " +
                "WHERE m.ProjectId = @id ORDER BY CASE m.Role WHEN N'owner' THEN 0 ELSE 1 END, m.AddedAtUtc, u.Email",
                connection);
            command.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));

            var list = new List<ProjectMember>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new ProjectMember(reader.GetGuid(0).ToString("N"), reader.GetString(1), reader.GetString(2), reader.GetString(3), AsUtc(reader.GetDateTime(4))));
            }

            return list;
        }, cancellationToken);

    public Task<bool> RemoveMemberAsync(string projectId, Guid userId, CancellationToken cancellationToken = default) =>
        WithConnectionAsync(async (connection, ct) =>
        {
            await using var command = new SqlCommand(
                "DELETE FROM dbo.ProjectMembers WHERE ProjectId = @id AND UserId = @user AND Role <> N'owner'", connection);
            command.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
            command.Parameters.Add(Param("@user", SqlDbType.UniqueIdentifier, 0, userId));
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }, cancellationToken);

    // ---- invites --------------------------------------------------------------------------------

    public async Task<CreateInviteResult> CreateInviteAsync(
        string projectId, Guid ownerId, string codeHash, DateTime expiresAtUtc, int maxUses, int maxLiveInvites, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        try
        {
            return await WithConnectionAsync(async (connection, ct) =>
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

                // Lock the project row (only if the caller owns it): serialises invite creation and doubles as the ownership check.
                await using (var owner = new SqlCommand(
                    "SELECT 1 FROM dbo.Projects WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id AND OwnerUserId = @owner", connection, transaction))
                {
                    owner.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
                    owner.Parameters.Add(Param("@owner", SqlDbType.UniqueIdentifier, 0, ownerId));
                    if (await owner.ExecuteScalarAsync(ct) is null)
                    {
                        return new CreateInviteResult(CreateInviteStatus.NotOwner, null);
                    }
                }

                await using (var live = new SqlCommand(
                    "SELECT COUNT(*) FROM dbo.ProjectInvites " +
                    "WHERE ProjectId = @id AND RevokedAtUtc IS NULL AND ExpiresAtUtc > @now AND UsedCount < MaxUses", connection, transaction))
                {
                    live.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
                    live.Parameters.Add(Param("@now", SqlDbType.DateTime2, 0, nowUtc));
                    if (Convert.ToInt32(await live.ExecuteScalarAsync(ct)) >= maxLiveInvites)
                    {
                        return new CreateInviteResult(CreateInviteStatus.LimitReached, null);
                    }
                }

                long id;
                await using (var insert = new SqlCommand(
                    "INSERT INTO dbo.ProjectInvites (ProjectId, CodeHash, CreatedByUserId, CreatedAtUtc, ExpiresAtUtc, MaxUses) " +
                    "OUTPUT inserted.Id VALUES (@id, @hash, @by, @now, @expires, @max)", connection, transaction))
                {
                    insert.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
                    insert.Parameters.Add(Param("@hash", SqlDbType.Char, 64, codeHash));
                    insert.Parameters.Add(Param("@by", SqlDbType.UniqueIdentifier, 0, ownerId));
                    insert.Parameters.Add(Param("@now", SqlDbType.DateTime2, 0, nowUtc));
                    insert.Parameters.Add(Param("@expires", SqlDbType.DateTime2, 0, expiresAtUtc));
                    insert.Parameters.Add(Param("@max", SqlDbType.Int, 0, maxUses));
                    id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
                }

                await transaction.CommitAsync(ct);
                return new CreateInviteResult(CreateInviteStatus.Created, new InviteInfo(id, expiresAtUtc, maxUses, 0, false));
            }, cancellationToken);
        }
        catch (SqlException ex) when (IsDuplicateKey(ex))
        {
            // The hash of this code already exists (2^-50): the caller draws another code.
            return new CreateInviteResult(CreateInviteStatus.CodeCollision, null);
        }
    }

    public Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(string projectId, CancellationToken cancellationToken = default) =>
        WithConnectionAsync<IReadOnlyList<InviteInfo>>(async (connection, ct) =>
        {
            await using var command = new SqlCommand(
                "SELECT Id, ExpiresAtUtc, MaxUses, UsedCount, RevokedAtUtc FROM dbo.ProjectInvites WHERE ProjectId = @id ORDER BY Id DESC", connection);
            command.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));

            var list = new List<InviteInfo>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new InviteInfo(reader.GetInt64(0), AsUtc(reader.GetDateTime(1)), reader.GetInt32(2), reader.GetInt32(3), !reader.IsDBNull(4)));
            }

            return list;
        }, cancellationToken);

    public Task<bool> RevokeInviteAsync(string projectId, Guid ownerId, long inviteId, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        WithConnectionAsync(async (connection, ct) =>
        {
            // Idempotent: revoking an already revoked invite keeps its first revocation time and still reports success.
            await using var command = new SqlCommand(
                "UPDATE i SET RevokedAtUtc = COALESCE(i.RevokedAtUtc, @now) " +
                "FROM dbo.ProjectInvites i JOIN dbo.Projects p ON p.Id = i.ProjectId " +
                "WHERE i.Id = @invite AND i.ProjectId = @id AND p.OwnerUserId = @owner", connection);
            command.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
            command.Parameters.Add(Param("@owner", SqlDbType.UniqueIdentifier, 0, ownerId));
            command.Parameters.Add(Param("@invite", SqlDbType.BigInt, 0, inviteId));
            command.Parameters.Add(Param("@now", SqlDbType.DateTime2, 0, nowUtc));
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }, cancellationToken);

    public async Task<RedeemResult> RedeemInviteAsync(Guid userId, string codeHash, int maxMembers, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await WithConnectionAsync(
                    (connection, ct) => RedeemOnceAsync(connection, userId, codeHash, maxMembers, nowUtc, ct),
                    cancellationToken);
            }
            catch (SqlException ex) when (IsDuplicateKey(ex) && attempt <= MaxDuplicateKeyRetries)
            {
                // Two requests added the same person at once; go round again and see them as a member.
                _logger?.LogInformation("Concurrent invite redemption for the same member; retrying.");
            }
        }
    }

    private static async Task<RedeemResult> RedeemOnceAsync(
        SqlConnection connection, Guid userId, string codeHash, int maxMembers, DateTime nowUtc, CancellationToken ct)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var invalid = new RedeemResult(RedeemStatus.InvalidInvite, null, null);

        // 1. The invite, locked: two redemptions of one code queue up here. Revoked and expired look like "not found".
        long inviteId;
        string projectId;
        int maxUses, usedCount;
        await using (var find = new SqlCommand(
            "SELECT Id, ProjectId, MaxUses, UsedCount FROM dbo.ProjectInvites WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE CodeHash = @hash AND RevokedAtUtc IS NULL AND ExpiresAtUtc > @now", connection, transaction))
        {
            find.Parameters.Add(Param("@hash", SqlDbType.Char, 64, codeHash));
            find.Parameters.Add(Param("@now", SqlDbType.DateTime2, 0, nowUtc));
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return invalid;
            }

            inviteId = reader.GetInt64(0);
            projectId = reader.GetString(1);
            maxUses = reader.GetInt32(2);
            usedCount = reader.GetInt32(3);
        }

        // 2. The project, locked: redemptions into one project (through any of its codes) are serialised,
        //    so the member count below is exact.
        string projectName;
        await using (var project = new SqlCommand(
            "SELECT Name FROM dbo.Projects WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id", connection, transaction))
        {
            project.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
            if (await project.ExecuteScalarAsync(ct) is not string name)
            {
                return invalid;
            }

            projectName = name;
        }

        // 3. Already a member: nothing is consumed.
        await using (var member = new SqlCommand(
            "SELECT 1 FROM dbo.ProjectMembers WHERE ProjectId = @id AND UserId = @user", connection, transaction))
        {
            member.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
            member.Parameters.Add(Param("@user", SqlDbType.UniqueIdentifier, 0, userId));
            if (await member.ExecuteScalarAsync(ct) is not null)
            {
                return new RedeemResult(RedeemStatus.AlreadyMember, projectId, projectName);
            }
        }

        if (usedCount >= maxUses)
        {
            return invalid;
        }

        // 4. Room for one more?
        await using (var count = new SqlCommand("SELECT COUNT(*) FROM dbo.ProjectMembers WHERE ProjectId = @id", connection, transaction))
        {
            count.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
            if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) >= maxMembers)
            {
                return new RedeemResult(RedeemStatus.ProjectFull, projectId, projectName);
            }
        }

        // 5. Consume a use. The WHERE clause is the real guard: it fails if anything changed since step 1.
        await using (var consume = new SqlCommand(
            "UPDATE dbo.ProjectInvites SET UsedCount = UsedCount + 1 " +
            "WHERE Id = @invite AND UsedCount < MaxUses AND RevokedAtUtc IS NULL AND ExpiresAtUtc > @now", connection, transaction))
        {
            consume.Parameters.Add(Param("@invite", SqlDbType.BigInt, 0, inviteId));
            consume.Parameters.Add(Param("@now", SqlDbType.DateTime2, 0, nowUtc));
            if (await consume.ExecuteNonQueryAsync(ct) == 0)
            {
                return invalid;
            }
        }

        try
        {
            await using var insert = new SqlCommand(
                "INSERT INTO dbo.ProjectMembers (ProjectId, UserId, Role) VALUES (@id, @user, N'member')", connection, transaction);
            insert.Parameters.Add(Param("@id", SqlDbType.NVarChar, 32, projectId));
            insert.Parameters.Add(Param("@user", SqlDbType.UniqueIdentifier, 0, userId));
            await insert.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number == ForeignKeyViolation)
        {
            return new RedeemResult(RedeemStatus.UnknownUser, null, null);
        }

        await transaction.CommitAsync(ct);
        return new RedeemResult(RedeemStatus.Redeemed, projectId, projectName);
    }

    // ---- plumbing -------------------------------------------------------------------------------

    private static async Task AcquireLockAsync(SqlConnection connection, SqlTransaction transaction, string resource, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "DECLARE @rc int; " +
            "EXEC @rc = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000; " +
            "IF @rc < 0 THROW 50001, 'Could not acquire the lock.', 1;",
            connection, transaction);
        command.Parameters.Add(Param("@resource", SqlDbType.NVarChar, 255, resource));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Opens a connection and runs the work, retrying transient failures (database resuming, throttling, deadlock).</summary>
    private async Task<T> WithConnectionAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                return await work(connection, cancellationToken);
            }
            catch (SqlException ex) when (attempt < MaxTransientAttempts && IsTransient(ex) && attempt - 1 < _retryDelays.Length)
            {
                var delay = _retryDelays[attempt - 1];
                _logger?.LogWarning("Transient SQL error {Number} (attempt {Attempt}); retrying in {Delay}s.", ex.Number, attempt, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsDuplicateKey(SqlException ex) => ex.Errors.Cast<SqlError>().Any(e => DuplicateKeyErrors.Contains(e.Number));

    private static bool IsTransient(SqlException ex) =>
        ex.IsTransient || ex.Errors.Cast<SqlError>().Any(e => TransientErrors.Contains(e.Number));

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static SqlParameter Param(string name, SqlDbType type, int size, object? value)
    {
        var parameter = size > 0 ? new SqlParameter(name, type, size) : new SqlParameter(name, type);
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }
}
