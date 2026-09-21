using System.Reflection;
using FootLook.Central.Data;
using FootLook.Central.Security;
using FootLook.Core.Models;
using FootLook.Data.Accounts;
using Microsoft.Data.SqlClient;

namespace FootLook.Central.Tests;

/// <summary>
/// Runs a test only when FOOTLOOK_TEST_SQL is set. Its value is a connection string to a database that already has
/// Sql/001_create_users.sql and Sql/002_projects.sql applied (the connection string is a secret: pass it through
/// the environment of the test process, never a file).
/// </summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "FOOTLOOK_TEST_SQL";

    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
        {
            Skip = $"Set {EnvironmentVariable} to a SQL connection string to run the central store integration tests.";
        }
    }
}

/// <summary>
/// The shared store contract against the real database. Every user is created through the real account store under
/// a fresh random tenant id with an @example.invalid email; everything the test made (projects and, by cascade,
/// their members / invites / return URLs; then the users and their login events) is deleted afterwards by tenant.
/// </summary>
internal sealed class SqlCentralStoreContract : CentralStoreContractTests
{
    private readonly string _connectionString = Environment.GetEnvironmentVariable(SqlFactAttribute.EnvironmentVariable)!;
    private readonly List<string> _tenants = new();
    private SqlCentralStore _store = null!;
    private SqlFootLookAccountStore _accounts = null!;

    protected override ICentralStore Store => _store;

    public string ConnectionString => _connectionString;

    public ICentralStore Db => _store;

    public Task<Guid> NewUser(string label) => NewUserAsync(label);

    public override Task InitializeAsync()
    {
        _store = new SqlCentralStore(_connectionString);
        _accounts = new SqlFootLookAccountStore(_connectionString);
        return Task.CompletedTask;
    }

    protected override async Task<Guid> NewUserAsync(string label)
    {
        var tenant = Guid.NewGuid().ToString("D");
        _tenants.Add(tenant);
        var result = await _accounts.SignInAsync(
            new MicrosoftIdentity(tenant, Guid.NewGuid().ToString("D"), $"{label}-{Guid.NewGuid():N}@example.invalid", label, "work"),
            createIfMissing: true, acceptedTerms: true, ipAddress: null, userAgent: null);
        return Guid.Parse(result.User!.Id);
    }

    public override async Task DisposeAsync()
    {
        if (_tenants.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        foreach (var tenant in _tenants)
        {
            await using var command = new SqlCommand(
                "DELETE FROM dbo.Projects WHERE OwnerUserId IN (SELECT Id FROM dbo.Users WHERE TenantId = @t); " +
                "DELETE FROM dbo.ProjectMembers WHERE UserId IN (SELECT Id FROM dbo.Users WHERE TenantId = @t); " +
                "DELETE FROM dbo.ProjectInvites WHERE CreatedByUserId IN (SELECT Id FROM dbo.Users WHERE TenantId = @t); " +
                "DELETE e FROM dbo.LoginEvents e JOIN dbo.Users u ON u.Id = e.UserId WHERE u.TenantId = @t; " +
                "DELETE FROM dbo.Users WHERE TenantId = @t;", connection);
            command.Parameters.AddWithValue("@t", tenant);
            await command.ExecuteNonQueryAsync();
        }
    }
}

public class SqlCentralStoreTests
{
    private static async Task<T> WithFixtureAsync<T>(Func<SqlCentralStoreContract, Task<T>> body)
    {
        var fixture = new SqlCentralStoreContract();
        await fixture.InitializeAsync();
        try
        {
            return await body(fixture);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>Runs every [Fact] of the shared contract against SQL, each on its own fresh data, and reports all failures at once.</summary>
    [SqlFact]
    public async Task The_shared_store_contract_holds_against_sql()
    {
        var failures = new List<string>();
        var methods = typeof(CentralStoreContractTests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
            .OrderBy(m => m.Name)
            .ToList();
        Assert.True(methods.Count >= 20, "The contract lost its tests?");

        foreach (var method in methods)
        {
            var fixture = new SqlCentralStoreContract();
            await fixture.InitializeAsync();
            try
            {
                await (Task)method.Invoke(fixture, null)!;
            }
            catch (TargetInvocationException ex)
            {
                // Type and message of the assertion only; the connection string is never part of either.
                failures.Add($"{method.Name}: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
            }
            finally
            {
                await fixture.DisposeAsync();
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {methods.Count} contract tests failed against SQL:\n" + string.Join("\n---\n", failures));
    }

    [SqlFact]
    public async Task Only_the_sha256_hash_of_a_code_is_in_the_table_never_the_code()
    {
        await WithFixtureAsync(async f =>
        {
            var owner = await Owner(f);
            var project = (await f.Db.CreateProjectAsync(owner, "H", Array.Empty<string>(), 10)).Project!.Id;
            var code = InviteCodes.Generate();
            var hash = InviteCodes.Hash(code);
            await f.Db.CreateInviteAsync(project, owner, hash, DateTime.UtcNow.AddHours(1), 1, 20, DateTime.UtcNow);

            var stored = await Scalar<string>(f, "SELECT CodeHash FROM dbo.ProjectInvites WHERE ProjectId = @p", ("@p", project));
            Assert.Equal(hash, stored);
            Assert.Matches("^[0-9a-f]{64}$", stored!);
            Assert.NotEqual(code, stored);

            // No column anywhere in the invites table could hold a raw code.
            var columns = await Column<string>(f, "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'ProjectInvites' AND TABLE_SCHEMA = 'dbo'");
            Assert.DoesNotContain(columns, c => c.Equals("Code", StringComparison.OrdinalIgnoreCase));
            var dump = await Column<string>(f, "SELECT CONCAT(ProjectId, '|', CodeHash, '|', CreatedByUserId) FROM dbo.ProjectInvites WHERE ProjectId = @p", ("@p", project));
            Assert.DoesNotContain(dump, row => row.Contains(code, StringComparison.OrdinalIgnoreCase) || row.Contains(code[3..], StringComparison.OrdinalIgnoreCase));
            return 0;
        });
    }

    [SqlFact]
    public async Task Deleting_a_project_cascades_in_the_database_itself()
    {
        await WithFixtureAsync(async f =>
        {
            var owner = await Owner(f);
            var member = await f.NewUser("member");
            var project = (await f.Db.CreateProjectAsync(owner, "C", new[] { "https://a.example/x" }, 10)).Project!.Id;
            var hash = InviteCodes.Hash(InviteCodes.Generate());
            await f.Db.CreateInviteAsync(project, owner, hash, DateTime.UtcNow.AddHours(1), 3, 20, DateTime.UtcNow);
            await f.Db.RedeemInviteAsync(member, hash, 25, DateTime.UtcNow);

            Assert.Equal(2, await Scalar<int>(f, "SELECT COUNT(*) FROM dbo.ProjectMembers WHERE ProjectId = @p", ("@p", project)));

            // Straight SQL delete, not through the store: the FKs themselves must cascade.
            await Run(f, "DELETE FROM dbo.Projects WHERE Id = @p", ("@p", project));

            Assert.Equal(0, await Scalar<int>(f, "SELECT COUNT(*) FROM dbo.ProjectMembers WHERE ProjectId = @p", ("@p", project)));
            Assert.Equal(0, await Scalar<int>(f, "SELECT COUNT(*) FROM dbo.ProjectInvites WHERE ProjectId = @p", ("@p", project)));
            Assert.Equal(0, await Scalar<int>(f, "SELECT COUNT(*) FROM dbo.ProjectReturnUrls WHERE ProjectId = @p", ("@p", project)));
            return 0;
        });
    }

    [SqlFact]
    public async Task The_schema_enforces_its_own_rules()
    {
        await WithFixtureAsync(async f =>
        {
            var owner = await Owner(f);
            var project = (await f.Db.CreateProjectAsync(owner, "S", Array.Empty<string>(), 10)).Project!.Id;
            var hash = InviteCodes.Hash(InviteCodes.Generate());
            await f.Db.CreateInviteAsync(project, owner, hash, DateTime.UtcNow.AddHours(1), 1, 20, DateTime.UtcNow);

            // Unique invite hash.
            var duplicate = await Assert.ThrowsAsync<SqlException>(() => Run(f,
                "INSERT INTO dbo.ProjectInvites (ProjectId, CodeHash, CreatedByUserId, ExpiresAtUtc, MaxUses) VALUES (@p, @h, @u, SYSUTCDATETIME(), 1)",
                ("@p", project), ("@h", hash), ("@u", owner)));
            Assert.Contains(duplicate.Number, new[] { 2601, 2627 });

            // Role is owner or member.
            var stranger = await f.NewUser("x");
            var role = await Assert.ThrowsAsync<SqlException>(() => Run(f,
                "INSERT INTO dbo.ProjectMembers (ProjectId, UserId, Role) VALUES (@p, @u, N'admin')", ("@p", project), ("@u", stranger)));
            Assert.Equal(547, role.Number);

            // A member row needs a real user and a real project.
            var noUser = await Assert.ThrowsAsync<SqlException>(() => Run(f,
                "INSERT INTO dbo.ProjectMembers (ProjectId, UserId, Role) VALUES (@p, @u, N'member')", ("@p", project), ("@u", Guid.NewGuid())));
            Assert.Equal(547, noUser.Number);
            var noProject = await Assert.ThrowsAsync<SqlException>(() => Run(f,
                "INSERT INTO dbo.ProjectMembers (ProjectId, UserId, Role) VALUES (N'prj_doesnotexist000', @u, N'member')", ("@u", owner)));
            Assert.Equal(547, noProject.Number);

            // The three child tables cascade; the owner FK does not (a user with projects cannot just vanish).
            var actions = await Column<string>(f,
                "SELECT OBJECT_NAME(parent_object_id) + ':' + delete_referential_action_desc FROM sys.foreign_keys " +
                "WHERE referenced_object_id = OBJECT_ID('dbo.Projects') ORDER BY 1");
            Assert.Equal(new[] { "ProjectInvites:CASCADE", "ProjectMembers:CASCADE", "ProjectReturnUrls:CASCADE" }, actions.ToArray());
            return 0;
        });
    }

    [SqlFact]
    public async Task Search_text_is_never_interpreted_as_sql()
    {
        await WithFixtureAsync(async f =>
        {
            var owner = await Owner(f);
            var before = await Scalar<int>(f, "SELECT COUNT(*) FROM dbo.Users");

            var result = await f.Db.CreateProjectAsync(owner, "x'; DELETE FROM dbo.Users;--", new[] { "https://a.example/'; DELETE FROM dbo.Users;--" }, 10);
            await f.Db.RedeemInviteAsync(owner, "' OR 1=1;--", 25, DateTime.UtcNow);
            await f.Db.GetProjectAsync("' OR '1'='1", owner);

            Assert.Equal(CreateProjectStatus.Created, result.Status);
            Assert.Equal(before, await Scalar<int>(f, "SELECT COUNT(*) FROM dbo.Users"));
            return 0;
        });
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static Task<Guid> Owner(SqlCentralStoreContract f) => f.NewUser("owner");

    private static async Task<T?> Scalar<T>(SqlCentralStoreContract f, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(f.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private static async Task<List<T>> Column<T>(SqlCentralStoreContract f, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(f.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var list = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add((T)reader.GetValue(0));
        }

        return list;
    }

    private static async Task Run(SqlCentralStoreContract f, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(f.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}

