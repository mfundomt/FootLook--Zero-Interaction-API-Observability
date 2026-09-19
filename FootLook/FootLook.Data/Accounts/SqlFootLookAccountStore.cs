using System.Data;
using FootLook.Core.Models;
using FootLook.Core.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FootLook.Data.Accounts;

/// <summary>
/// Microsoft (Entra ID) accounts in Azure SQL (schema: Sql/001_create_users.sql). Plain ADO.NET,
/// parameterised SQL only.
/// <para>
/// Identity is (TenantId, Subject) - the unique index UX_Users_Tenant_Subject - and never the
/// email. A sign-in is one transaction: it locks that key (UPDLOCK + HOLDLOCK, which also
/// locks the "no such row yet" range), then updates the row or inserts it. Should a duplicate
/// key still slip through, the unique index rejects it and the whole sign-in is retried, at
/// which point the row exists.
/// </para>
/// <para>
/// The first account ever created becomes admin. That decision is taken under an exclusive
/// transaction-scoped application lock (sp_getapplock), so two simultaneous first
/// registrations serialize: the second one sees the first's committed row and is not admin.
/// </para>
/// <para>
/// The database is serverless and auto-pauses, so the first connection after idle can take
/// about a minute (use Connection Timeout=60 in the connection string) and may fail with a
/// transient error while it resumes; those are retried a few times.
/// </para>
/// </summary>
public sealed class SqlFootLookAccountStore : IFootLookMicrosoftAccountStore
{
    private const int MaxTransientAttempts = 4;
    private const int MaxDuplicateKeyRetries = 3;
    private const int IpAddressLength = 64;
    private const int UserAgentLength = 400;

    private static readonly int[] DuplicateKeyErrors = { 2601, 2627 };

    // Errors that mean "try again": database resuming/unavailable, throttling, failover,
    // deadlock victim, timeouts and connection drops.
    private static readonly int[] TransientErrors =
    {
        40613, 40197, 40501, 49918, 49919, 49920, 4060, 10928, 10929, 1205, -2, 64, 10053, 10054, 10060, 233,
    };

    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
    };

    private readonly string _connectionString;
    private readonly ILogger? _logger;
    private readonly TimeSpan[] _retryDelays;

    public SqlFootLookAccountStore(string connectionString, ILogger<SqlFootLookAccountStore>? logger = null)
        : this(connectionString, logger, RetryDelays)
    {
    }

    internal SqlFootLookAccountStore(string connectionString, ILogger? logger, TimeSpan[] retryDelays)
    {
        _connectionString = connectionString;
        _logger = logger;
        _retryDelays = retryDelays;
    }

    public async Task<MicrosoftSignInResult> SignInAsync(
        MicrosoftIdentity identity,
        bool createIfMissing,
        bool acceptedTerms,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await WithConnectionAsync(
                    (connection, ct) => SignInOnceAsync(connection, identity, createIfMissing, acceptedTerms, ipAddress, userAgent, ct),
                    cancellationToken);
            }
            catch (SqlException ex) when (IsDuplicateKey(ex) && attempt <= MaxDuplicateKeyRetries)
            {
                // Lost a race to create the same identity; the row exists now, so go round again.
                _logger?.LogInformation("Concurrent creation of the same Microsoft identity; retrying sign-in.");
            }
        }
    }

    public async Task<FootLookUser?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var userId))
        {
            return null;
        }

        return await WithConnectionAsync(async (connection, ct) =>
        {
            await using var command = new SqlCommand(
                "SELECT Id, Email, DisplayName, IsAdmin, CreatedAtUtc FROM dbo.Users WHERE Id = @id", connection);
            command.Parameters.Add(Param("@id", SqlDbType.UniqueIdentifier, 0, userId));

            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
        }, cancellationToken);
    }

    private static async Task<MicrosoftSignInResult> SignInOnceAsync(
        SqlConnection connection,
        MicrosoftIdentity identity,
        bool createIfMissing,
        bool acceptedTerms,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        // Disposing the transaction without committing rolls it back, so every early return
        // and every exception below leaves nothing behind.
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        FootLookUser? user;
        var existing = false;

        await using (var find = new SqlCommand(
            // UPDLOCK + HOLDLOCK: hold the key (or the empty range where it would be) until
            // commit, so a concurrent sign-in for the same identity waits for us.
            "SELECT Id, Email, DisplayName, IsAdmin, CreatedAtUtc FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE TenantId = @tenant AND Subject = @subject",
            connection, transaction))
        {
            AddIdentityParameters(find, identity);
            await using var reader = await find.ExecuteReaderAsync(ct);
            user = await reader.ReadAsync(ct) ? ReadUser(reader) : null;
            existing = user is not null;
        }

        if (existing)
        {
            await using var update = new SqlCommand(
                "UPDATE dbo.Users SET LastLoginAtUtc = SYSUTCDATETIME(), LoginCount = LoginCount + 1, " +
                "Email = @email, DisplayName = @name, " +
                "AcceptedTermsAtUtc = CASE WHEN AcceptedTermsAtUtc IS NULL AND @terms = 1 THEN SYSUTCDATETIME() ELSE AcceptedTermsAtUtc END " +
                "WHERE Id = @id",
                connection, transaction);
            update.Parameters.Add(Param("@id", SqlDbType.UniqueIdentifier, 0, Guid.Parse(user!.Id)));
            update.Parameters.Add(Param("@email", SqlDbType.NVarChar, 320, identity.Email));
            update.Parameters.Add(Param("@name", SqlDbType.NVarChar, 200, identity.DisplayName));
            update.Parameters.Add(Param("@terms", SqlDbType.Bit, 0, acceptedTerms));
            await update.ExecuteNonQueryAsync(ct);

            user = user with { Email = identity.Email, DisplayName = identity.DisplayName };
        }
        else
        {
            if (!createIfMissing)
            {
                return new MicrosoftSignInResult(MicrosoftSignInStatus.NotFound, null);
            }

            // Serialize "am I the first account?" across every concurrent creation. The lock
            // is released when the transaction ends, i.e. after our row is committed.
            await using var admin = new SqlCommand(
                "DECLARE @rc int; " +
                "EXEC @rc = sp_getapplock @Resource = N'footlook:first-admin', @LockMode = 'Exclusive', " +
                "     @LockOwner = 'Transaction', @LockTimeout = 30000; " +
                "IF @rc < 0 THROW 50001, 'Could not acquire the first-admin lock.', 1; " +
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Users) THEN 0 ELSE 1 END;",
                connection, transaction);
            var isAdmin = Convert.ToInt32(await admin.ExecuteScalarAsync(ct)) == 1;

            var id = Guid.NewGuid();
            DateTime createdAtUtc;
            await using (var insert = new SqlCommand(
                "INSERT INTO dbo.Users (Id, TenantId, Subject, Email, DisplayName, AccountType, IsAdmin, AcceptedTermsAtUtc, LastLoginAtUtc, LoginCount) " +
                "OUTPUT inserted.CreatedAtUtc " +
                "VALUES (@id, @tenant, @subject, @email, @name, @type, @admin, " +
                "        CASE WHEN @terms = 1 THEN SYSUTCDATETIME() ELSE NULL END, SYSUTCDATETIME(), 1)",
                connection, transaction))
            {
                AddIdentityParameters(insert, identity);
                insert.Parameters.Add(Param("@id", SqlDbType.UniqueIdentifier, 0, id));
                insert.Parameters.Add(Param("@email", SqlDbType.NVarChar, 320, identity.Email));
                insert.Parameters.Add(Param("@name", SqlDbType.NVarChar, 200, identity.DisplayName));
                insert.Parameters.Add(Param("@type", SqlDbType.NVarChar, 16, identity.AccountType));
                insert.Parameters.Add(Param("@admin", SqlDbType.Bit, 0, isAdmin));
                insert.Parameters.Add(Param("@terms", SqlDbType.Bit, 0, acceptedTerms));
                createdAtUtc = DateTime.SpecifyKind((DateTime)(await insert.ExecuteScalarAsync(ct))!, DateTimeKind.Utc);
            }

            user = new FootLookUser(
                Id: id.ToString("N"),
                Email: identity.Email,
                DisplayName: identity.DisplayName,
                // No password: FootLookPasswordHasher.Verify() rejects an empty hash, so
                // password login can never succeed for a Microsoft account.
                PasswordHash: string.Empty,
                IsAdmin: isAdmin,
                CreatedAtUtc: createdAtUtc);
        }

        await using (var log = new SqlCommand(
            "INSERT INTO dbo.LoginEvents (UserId, Outcome, IpAddress, UserAgent) VALUES (@user, @outcome, @ip, @agent)",
            connection, transaction))
        {
            log.Parameters.Add(Param("@user", SqlDbType.UniqueIdentifier, 0, Guid.Parse(user.Id)));
            log.Parameters.Add(Param("@outcome", SqlDbType.NVarChar, 32, existing ? "login" : "register"));
            log.Parameters.Add(Param("@ip", SqlDbType.NVarChar, IpAddressLength, Truncate(ipAddress, IpAddressLength)));
            log.Parameters.Add(Param("@agent", SqlDbType.NVarChar, UserAgentLength, Truncate(userAgent, UserAgentLength)));
            await log.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        return new MicrosoftSignInResult(existing ? MicrosoftSignInStatus.SignedIn : MicrosoftSignInStatus.Registered, user);
    }

    /// <summary>Opens a connection and runs the work, retrying transient failures (paused database resuming, throttling, deadlock).</summary>
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

    private static void AddIdentityParameters(SqlCommand command, MicrosoftIdentity identity)
    {
        command.Parameters.Add(Param("@tenant", SqlDbType.NVarChar, 64, identity.TenantId));
        command.Parameters.Add(Param("@subject", SqlDbType.NVarChar, 128, identity.Subject));
    }

    private static SqlParameter Param(string name, SqlDbType type, int size, object? value)
    {
        var parameter = size > 0 ? new SqlParameter(name, type, size) : new SqlParameter(name, type);
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= maxLength ? value : value[..maxLength];

    // Column order: Id, Email, DisplayName, IsAdmin, CreatedAtUtc.
    private static FootLookUser ReadUser(SqlDataReader reader) => new(
        Id: reader.GetGuid(0).ToString("N"),
        Email: reader.GetString(1),
        DisplayName: reader.GetString(2),
        PasswordHash: string.Empty,
        IsAdmin: reader.GetBoolean(3),
        CreatedAtUtc: DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
}
