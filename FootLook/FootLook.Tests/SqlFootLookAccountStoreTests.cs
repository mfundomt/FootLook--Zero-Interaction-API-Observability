using FootLook.Core.Models;
using FootLook.Core.Security;
using FootLook.Data.Accounts;
using Microsoft.Data.SqlClient;

namespace FootLook.Tests;

/// <summary>
/// Runs a test only when FOOTLOOK_TEST_SQL is set. Its value is a connection string to a
/// database that already has Sql/001_create_users.sql applied (the connection string is a
/// secret: pass it through the environment of the test process, never a file).
/// </summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "FOOTLOOK_TEST_SQL";

    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
        {
            Skip = $"Set {EnvironmentVariable} to a SQL connection string to run the SQL account store integration tests.";
        }
    }
}

/// <summary>
/// Integration tests for <see cref="SqlFootLookAccountStore"/> against a real database. Every
/// identity uses a fresh random tenant id and an @example.invalid email; each test's rows
/// (users and their login events) are deleted afterwards by tenant id.
/// </summary>
public class SqlFootLookAccountStoreTests : IAsyncLifetime
{
    private readonly string? _connectionString = Environment.GetEnvironmentVariable(SqlFactAttribute.EnvironmentVariable);
    private readonly List<string> _tenants = new();
    private SqlFootLookAccountStore _store = null!;

    public Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            _store = new SqlFootLookAccountStore(_connectionString);
        }

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || _tenants.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        foreach (var tenant in _tenants)
        {
            await using var command = new SqlCommand(
                "DELETE e FROM dbo.LoginEvents e JOIN dbo.Users u ON u.Id = e.UserId WHERE u.TenantId = @t; " +
                "DELETE FROM dbo.Users WHERE TenantId = @t;", connection);
            command.Parameters.AddWithValue("@t", tenant);
            await command.ExecuteNonQueryAsync();
        }
    }

    private MicrosoftIdentity NewIdentity(string? tenant = null, string? subject = null, string? email = null, string name = "Sql Test")
    {
        tenant ??= Guid.NewGuid().ToString("D");
        if (!_tenants.Contains(tenant))
        {
            _tenants.Add(tenant);
        }

        return new MicrosoftIdentity(
            tenant,
            subject ?? Guid.NewGuid().ToString("D"),
            email ?? $"{Guid.NewGuid():N}@example.invalid",
            name,
            "work");
    }

    private async Task<List<object?[]>> QueryAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private async Task<int> CountUsersAsync() => (int)(await QueryAsync("SELECT COUNT(*) FROM dbo.Users"))[0][0]!;

    [SqlFact]
    public async Task Register_creates_the_account_and_a_register_event()
    {
        var identity = NewIdentity();
        var result = await _store.SignInAsync(identity, createIfMissing: true, acceptedTerms: true, "203.0.113.7", "SqlTests/1.0");

        Assert.Equal(MicrosoftSignInStatus.Registered, result.Status);
        var user = result.User!;
        Assert.Equal(identity.Email, user.Email);
        Assert.Equal(identity.DisplayName, user.DisplayName);
        Assert.Equal(string.Empty, user.PasswordHash);
        Assert.True(DateTime.UtcNow - user.CreatedAtUtc < TimeSpan.FromMinutes(5));

        var rows = await QueryAsync(
            "SELECT TenantId, Subject, Email, DisplayName, AccountType, IsAdmin, AcceptedTermsAtUtc, LastLoginAtUtc, LoginCount FROM dbo.Users WHERE Id = @id",
            ("@id", Guid.Parse(user.Id)));
        var row = Assert.Single(rows);
        Assert.Equal(identity.TenantId, row[0]);
        Assert.Equal(identity.Subject, row[1]);
        Assert.Equal(identity.Email, row[2]);
        Assert.Equal("work", row[4]);
        Assert.NotNull(row[6]);
        Assert.NotNull(row[7]);
        Assert.Equal(1, row[8]);

        var events = await QueryAsync("SELECT Outcome, IpAddress, UserAgent FROM dbo.LoginEvents WHERE UserId = @id", ("@id", Guid.Parse(user.Id)));
        var ev = Assert.Single(events);
        Assert.Equal("register", ev[0]);
        Assert.Equal("203.0.113.7", ev[1]);
        Assert.Equal("SqlTests/1.0", ev[2]);
    }

    [SqlFact]
    public async Task Account_can_be_read_back_by_id()
    {
        var created = (await _store.SignInAsync(NewIdentity(), true, true, null, null)).User!;

        var found = await _store.FindByIdAsync(created.Id);

        Assert.Equal(created, found);
        Assert.Null(await _store.FindByIdAsync(Guid.NewGuid().ToString("N")));
        Assert.Null(await _store.FindByIdAsync("not-a-guid"));
    }

    [SqlFact]
    public async Task Signing_in_again_bumps_counters_and_records_a_login_event_and_refreshes_profile()
    {
        var identity = NewIdentity();
        var first = (await _store.SignInAsync(identity, true, true, "203.0.113.7", "agent")).User!;

        var changed = identity with { Email = $"{Guid.NewGuid():N}@example.invalid", DisplayName = "Renamed" };
        var again = await _store.SignInAsync(changed, createIfMissing: false, acceptedTerms: false, "203.0.113.8", "agent2");

        Assert.Equal(MicrosoftSignInStatus.SignedIn, again.Status);
        Assert.Equal(first.Id, again.User!.Id);
        Assert.Equal(changed.Email, again.User.Email);
        Assert.Equal("Renamed", again.User.DisplayName);

        var row = Assert.Single(await QueryAsync(
            "SELECT Email, DisplayName, LoginCount FROM dbo.Users WHERE Id = @id", ("@id", Guid.Parse(first.Id))));
        Assert.Equal(changed.Email, row[0]);
        Assert.Equal("Renamed", row[1]);
        Assert.Equal(2, row[2]);

        var outcomes = (await QueryAsync("SELECT Outcome FROM dbo.LoginEvents WHERE UserId = @id ORDER BY Id", ("@id", Guid.Parse(first.Id))))
            .Select(r => (string)r[0]!).ToArray();
        Assert.Equal(new[] { "register", "login" }, outcomes);
    }

    [SqlFact]
    public async Task Registering_an_existing_identity_signs_in_and_keeps_the_original_account()
    {
        var identity = NewIdentity();
        var first = (await _store.SignInAsync(identity, true, true, null, null)).User!;

        var second = await _store.SignInAsync(identity, createIfMissing: true, acceptedTerms: true, null, null);

        Assert.Equal(MicrosoftSignInStatus.SignedIn, second.Status);
        Assert.Equal(first.Id, second.User!.Id);
        Assert.Equal(1, (await QueryAsync("SELECT COUNT(*) FROM dbo.Users WHERE TenantId = @t", ("@t", identity.TenantId)))[0][0]);
    }

    [SqlFact]
    public async Task Login_for_an_unknown_identity_is_not_found_and_creates_nothing()
    {
        var identity = NewIdentity();

        var result = await _store.SignInAsync(identity, createIfMissing: false, acceptedTerms: false, null, null);

        Assert.Equal(MicrosoftSignInStatus.NotFound, result.Status);
        Assert.Null(result.User);
        Assert.Equal(0, (await QueryAsync("SELECT COUNT(*) FROM dbo.Users WHERE TenantId = @t", ("@t", identity.TenantId)))[0][0]);
    }

    [SqlFact]
    public async Task Identity_is_tenant_and_subject_never_the_email()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var a = NewIdentity(email: email);
        var otherSubject = NewIdentity(tenant: a.TenantId, email: email);
        var otherTenant = NewIdentity(subject: a.Subject, email: email);

        var ids = new HashSet<string>
        {
            (await _store.SignInAsync(a, true, true, null, null)).User!.Id,
            (await _store.SignInAsync(otherSubject, true, true, null, null)).User!.Id,
            (await _store.SignInAsync(otherTenant, true, true, null, null)).User!.Id,
        };

        Assert.Equal(3, ids.Count);
    }

    [SqlFact]
    public async Task Concurrent_sign_ins_of_one_new_identity_create_one_account()
    {
        var identity = NewIdentity();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => _store.SignInAsync(identity, true, true, null, null))));

        Assert.Single(results.Select(r => r.User!.Id).Distinct());
        Assert.Equal(1, results.Count(r => r.Status == MicrosoftSignInStatus.Registered));
        Assert.Equal(7, results.Count(r => r.Status == MicrosoftSignInStatus.SignedIn));

        var id = Guid.Parse(results[0].User!.Id);
        Assert.Equal(1, (await QueryAsync("SELECT COUNT(*) FROM dbo.Users WHERE TenantId = @t", ("@t", identity.TenantId)))[0][0]);
        Assert.Equal(8, (await QueryAsync("SELECT LoginCount FROM dbo.Users WHERE Id = @id", ("@id", id)))[0][0]);
        Assert.Equal(8, (await QueryAsync("SELECT COUNT(*) FROM dbo.LoginEvents WHERE UserId = @id", ("@id", id)))[0][0]);
        Assert.Equal(1, (await QueryAsync("SELECT COUNT(*) FROM dbo.LoginEvents WHERE UserId = @id AND Outcome = 'register'", ("@id", id)))[0][0]);
    }

    [SqlFact]
    public async Task Only_the_very_first_account_on_an_empty_instance_becomes_admin_even_when_created_concurrently()
    {
        var existingBefore = await CountUsersAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => _store.SignInAsync(NewIdentity(), true, true, null, null))));

        var admins = results.Count(r => r.User!.IsAdmin);
        // On an instance that already has accounts none of these may be admin; on an empty one exactly one is.
        Assert.Equal(existingBefore == 0 ? 1 : 0, admins);
    }

    [SqlFact]
    public async Task Terms_acceptance_is_recorded_only_when_registering_with_it()
    {
        var withoutTerms = NewIdentity();
        var user = (await _store.SignInAsync(withoutTerms, true, acceptedTerms: false, null, null)).User!;
        Assert.Null((await QueryAsync("SELECT AcceptedTermsAtUtc FROM dbo.Users WHERE Id = @id", ("@id", Guid.Parse(user.Id))))[0][0]);

        await _store.SignInAsync(withoutTerms, true, acceptedTerms: true, null, null);
        Assert.NotNull((await QueryAsync("SELECT AcceptedTermsAtUtc FROM dbo.Users WHERE Id = @id", ("@id", Guid.Parse(user.Id))))[0][0]);
    }

    [SqlFact]
    public async Task Ip_and_user_agent_are_truncated_to_their_column_lengths()
    {
        var user = (await _store.SignInAsync(NewIdentity(), true, true, new string('1', 200), new string('u', 1000))).User!;

        var ev = Assert.Single(await QueryAsync("SELECT IpAddress, UserAgent FROM dbo.LoginEvents WHERE UserId = @id", ("@id", Guid.Parse(user.Id))));
        Assert.Equal(64, ((string)ev[0]!).Length);
        Assert.Equal(400, ((string)ev[1]!).Length);
    }

    [SqlFact]
    public async Task Hostile_values_are_stored_literally_because_sql_is_parameterised()
    {
        var name = "x'); DROP TABLE dbo.LoginEvents;--";
        var identity = NewIdentity(subject: "s' OR '1'='1", name: name);

        var user = (await _store.SignInAsync(identity, true, true, "1' OR 1=1--", "'; DELETE FROM dbo.Users;--")).User!;

        Assert.Equal(name, (await _store.FindByIdAsync(user.Id))!.DisplayName);
        Assert.Equal("s' OR '1'='1", (await QueryAsync("SELECT Subject FROM dbo.Users WHERE Id = @id", ("@id", Guid.Parse(user.Id))))[0][0]);
    }

    [Fact]
    public void Password_login_can_never_succeed_for_a_microsoft_account()
    {
        // These accounts have an empty PasswordHash; the hasher must reject it for any input.
        Assert.False(FootLookPasswordHasher.Verify("", string.Empty));
        Assert.False(FootLookPasswordHasher.Verify("Passw0rd!123", string.Empty));
        Assert.False(FootLookPasswordHasher.Verify("", null));
    }
}
