using System.Net;
using System.Text.Json;

namespace FootLook.Tests;

public class AuthEndpointTests : IAsyncLifetime
{
    private FootLookTestHost _host = null!;

    public async Task InitializeAsync() => _host = await FootLookTestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // ---- register -----------------------------------------------------------------

    [Fact]
    public async Task Register_returns_201_with_user_summary_and_no_token_or_hash()
    {
        var response = await _host.RegisterAsync("Dev@Example.com", displayName: "  Dev One ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/footlook/auth/me", response.Headers.Location?.ToString());

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FootLookTestHost.Password, raw);
        Assert.DoesNotContain("v1.", raw);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.Equal("dev@example.com", root.GetProperty("email").GetString());
        Assert.Equal("Dev One", root.GetProperty("displayName").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("id").GetString()));
        Assert.True(root.TryGetProperty("createdAtUtc", out _));
    }

    [Fact]
    public async Task Register_defaults_display_name_to_email_local_part()
    {
        var response = await _host.RegisterAsync("jane.doe@example.com");

        using var doc = await FootLookTestHost.ReadJsonAsync(response);
        Assert.Equal("jane.doe", doc.RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task First_account_is_admin_and_second_is_not()
    {
        using var first = await FootLookTestHost.ReadJsonAsync(await _host.RegisterAsync("first@example.com"));
        using var second = await FootLookTestHost.ReadJsonAsync(await _host.RegisterAsync("second@example.com"));

        Assert.True(first.RootElement.GetProperty("isAdmin").GetBoolean());
        Assert.False(second.RootElement.GetProperty("isAdmin").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("missing-tld@localhost")]
    [InlineData("two@@example.com")]
    [InlineData("Display Name <a@example.com>")]
    public async Task Register_rejects_invalid_email_with_400(string email)
    {
        var response = await _host.RegisterAsync(email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_rejects_email_longer_than_254_chars()
    {
        var email = new string('a', 250) + "@example.com";

        var response = await _host.RegisterAsync(email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(129)]
    [InlineData(5000)]
    public async Task Register_rejects_password_outside_8_to_128_chars(int length)
    {
        var response = await _host.RegisterAsync("dev@example.com", new string('x', length));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(128)]
    public async Task Register_accepts_password_at_the_length_boundaries(int length)
    {
        var response = await _host.RegisterAsync($"len{length}@example.com", new string('x', length));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Register_rejects_overlong_display_name()
    {
        var response = await _host.RegisterAsync("dev@example.com", displayName: new string('n', 101));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_duplicate_email_is_409_case_insensitively()
    {
        Assert.Equal(HttpStatusCode.Created, (await _host.RegisterAsync("dev@example.com")).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await _host.RegisterAsync("dev@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _host.RegisterAsync("DEV@Example.COM")).StatusCode);
    }

    [Fact]
    public async Task Register_is_403_when_registration_is_closed()
    {
        await using var closed = await FootLookTestHost.StartAsync(o => o.AllowRegistration = false);

        var response = await closed.RegisterAsync("dev@example.com");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- login --------------------------------------------------------------------

    [Fact]
    public async Task Login_returns_token_expiry_and_user_summary()
    {
        await _host.RegisterAsync("dev@example.com", displayName: "Dev");

        var before = DateTime.UtcNow;
        var response = await _host.LoginAsync("dev@example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("token").GetString()!.Split('.').Length); // a JWT
        Assert.InRange(root.GetProperty("expiresAtUtc").GetDateTime(), before.AddHours(7.9), before.AddHours(8.1));
        Assert.Equal("dev@example.com", root.GetProperty("user").GetProperty("email").GetString());
        Assert.True(root.GetProperty("user").GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task Login_email_match_is_case_insensitive_and_trimmed()
    {
        await _host.RegisterAsync("dev@example.com");

        Assert.Equal(HttpStatusCode.OK, (await _host.LoginAsync("DEV@EXAMPLE.COM")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.LoginAsync("  Dev@Example.com ")).StatusCode);
    }

    [Fact]
    public async Task Login_gives_401_with_identical_message_for_unknown_email_and_wrong_password()
    {
        await _host.RegisterAsync("dev@example.com");

        var unknown = await _host.LoginAsync("nobody@example.com");
        var wrongPassword = await _host.LoginAsync("dev@example.com", "totally-wrong-password");

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);

        var unknownBody = await unknown.Content.ReadAsStringAsync();
        var wrongBody = await wrongPassword.Content.ReadAsStringAsync();
        Assert.Equal(unknownBody, wrongBody);
        Assert.Contains("Invalid email or password", unknownBody);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(null, null)]
    [InlineData("dev@example.com", "")]
    [InlineData("", "somepassword")]
    public async Task Login_with_missing_credentials_is_401(string? email, string? password)
    {
        await _host.RegisterAsync("dev@example.com");

        var response = await _host.PostAsync("/footlook/auth/login", body: new { email, password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_overlong_password_is_401_not_a_server_error()
    {
        await _host.RegisterAsync("dev@example.com");

        var response = await _host.LoginAsync("dev@example.com", new string('x', 10_000));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- me / logout --------------------------------------------------------------

    [Fact]
    public async Task Me_returns_the_presented_token_session_and_active_observation()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");

        var response = await _host.GetAsync("/footlook/auth/me", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.Equal(token, root.GetProperty("token").GetString());
        Assert.Equal("dev@example.com", root.GetProperty("user").GetProperty("email").GetString());
        Assert.True(root.GetProperty("observationActive").GetBoolean());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("sessionId").GetString()));
        Assert.True(root.GetProperty("tokenExpiresAtUtc").GetDateTime() > DateTime.UtcNow);
        Assert.True(root.GetProperty("sessionStartedAtUtc").GetDateTime() <= DateTime.UtcNow);
    }

    [Fact]
    public async Task Each_login_gets_its_own_session_id()
    {
        await _host.RegisterAsync("dev@example.com");
        var t1 = await _host.LoginTokenAsync("dev@example.com");
        var t2 = await _host.LoginTokenAsync("dev@example.com");

        using var m1 = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/auth/me", t1));
        using var m2 = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/auth/me", t2));

        Assert.NotEqual(m1.RootElement.GetProperty("sessionId").GetString(), m2.RootElement.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Me_is_401_without_a_token_or_with_garbage()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/auth/me", "garbage")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/auth/me", "a.b.c")).StatusCode);
    }

    [Fact]
    public async Task Token_signed_with_a_different_key_is_rejected()
    {
        await using var other = await FootLookTestHost.StartAsync(o => o.TokenSigningKey = "a-completely-different-signing-key-0123456789");
        var foreignToken = await other.RegisterAndLoginAsync("dev@example.com");

        var response = await _host.GetAsync("/footlook/auth/me", foreignToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_token_everywhere()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");
        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync("/footlook/auth/me", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync("/footlook/captures", token)).StatusCode);

        var logout = await _host.PostAsync("/footlook/auth/logout", token);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/auth/me", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/captures", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/captures/stats", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.PostAsync("/footlook/auth/logout", token)).StatusCode);
    }

    [Fact]
    public async Task Logout_only_ends_the_session_it_was_called_with()
    {
        await _host.RegisterAsync("dev@example.com");
        var t1 = await _host.LoginTokenAsync("dev@example.com");
        var t2 = await _host.LoginTokenAsync("dev@example.com");

        await _host.PostAsync("/footlook/auth/logout", t1);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/auth/me", t1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync("/footlook/auth/me", t2)).StatusCode);
    }

    [Fact]
    public async Task Expired_session_invalidates_the_token_even_though_the_jwt_itself_is_still_within_clock_skew()
    {
        await using var shortLived = await FootLookTestHost.StartAsync(o => o.TokenLifetimeHours = 0.0002); // ~0.7s
        var token = await shortLived.RegisterAndLoginAsync("dev@example.com");

        await Task.Delay(1000);

        Assert.Equal(HttpStatusCode.Unauthorized, (await shortLived.GetAsync("/footlook/auth/me", token)).StatusCode);
    }

    // ---- authorization surface ----------------------------------------------------

    public static IEnumerable<object[]> ProtectedGetRoutes() => new[]
    {
        "/footlook/auth/me",
        "/footlook/captures",
        "/footlook/captures/stats",
        "/footlook/captures/recent",
        "/footlook/captures/history",
        "/footlook/captures/status",
        "/footlook/captures/" + Guid.NewGuid(),
        "/footlook/captures/identity?footlookSessionId=s&footlookTabId=t",
        "/footlook/dashboard/ops",
        "/footlook/dev/diagnostics",
        "/footlook/outcomes/metrics",
        "/footlook/privacy/status",
        "/footlook/privacy/audit",
        "/footlook/reliability/status",
        "/footlook/operations/health",
    }.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(ProtectedGetRoutes))]
    public async Task Protected_routes_are_401_without_a_token_and_reachable_with_one(string route)
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync(route, "garbage")).StatusCode);

        var authed = await _host.GetAsync(route, token);
        Assert.NotEqual(HttpStatusCode.Unauthorized, authed.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, authed.StatusCode);
    }

    [Fact]
    public async Task Protected_write_routes_are_401_without_a_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.SendAsync(HttpMethod.Delete, "/footlook/captures")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.PostAsync("/footlook/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.PostAsync("/footlook/pause")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.PostAsync("/footlook/resume")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.PostAsync("/footlook/dev/self-heal")).StatusCode);
    }

    [Fact]
    public async Task Health_register_and_login_are_anonymous()
    {
        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync("/footlook/health")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await _host.RegisterAsync("dev@example.com")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await _host.LoginAsync("dev@example.com")).StatusCode);
    }

    [Fact]
    public async Task Health_does_not_leak_capture_data()
    {
        var raw = await (await _host.GetAsync("/footlook/health")).Content.ReadAsStringAsync();

        Assert.Contains("Healthy", raw);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_routes_are_403_for_a_non_admin_and_200_for_the_admin()
    {
        var adminToken = await _host.RegisterAndLoginAsync("admin@example.com");
        await _host.RegisterAsync("user@example.com");
        var userToken = await _host.LoginTokenAsync("user@example.com");

        foreach (var route in new[] { "/footlook/pause", "/footlook/resume", "/footlook/captures/pause", "/footlook/captures/resume" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _host.PostAsync(route, userToken)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await _host.PostAsync(route, adminToken)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await _host.SendAsync(HttpMethod.Delete, "/footlook/privacy/audit", userToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.SendAsync(HttpMethod.Delete, "/footlook/privacy/audit", adminToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.PostAsync("/footlook/dev/self-heal", userToken)).StatusCode);
    }

    [Fact]
    public async Task Non_admin_403_body_is_json_message()
    {
        await _host.RegisterAsync("admin@example.com");
        await _host.RegisterAsync("user@example.com");
        var userToken = await _host.LoginTokenAsync("user@example.com");

        var response = await _host.PostAsync("/footlook/pause", userToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("admin", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pausing_as_admin_stops_capture_and_resuming_restarts_it()
    {
        var token = await _host.RegisterAndLoginAsync("admin@example.com");

        await _host.PostAsync("/footlook/pause", token);
        await _host.GetAsync("/hello");
        await _host.PostAsync("/footlook/resume", token);
        await _host.GetAsync("/other");

        await _host.WaitForCaptureAsync(token, "/other");
        Assert.DoesNotContain("/hello", await _host.GetCapturePathsAsync(token));
    }
}
