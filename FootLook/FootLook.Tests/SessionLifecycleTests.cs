using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using FootLook.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Tests;

/// <summary>
/// An observation session starts at sign-in and ends at sign-out (or expiry); each has its own
/// capture set, which is deleted when the session ends - even for the same account signing in again.
/// </summary>
public class SessionLifecycleTests : IAsyncLifetime
{
    private FootLookTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await FootLookTestHost.StartAsync();
        Assert.Equal(HttpStatusCode.Created, (await _host.RegisterAsync("a@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _host.RegisterAsync("b@example.com")).StatusCode);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static List<string> StorePaths(FootLookTestHost host) => host.Store.GetAll().Select(c => c.Path).ToList();

    // (a) ---------------------------------------------------------------------------

    [Fact]
    public async Task Same_account_signing_out_and_back_in_sees_none_of_its_old_captures()
    {
        var first = await _host.LoginTokenAsync("a@example.com");
        await _host.GetAsync("/hello");
        await _host.WaitForCaptureAsync(first, "/hello");

        await _host.PostAsync("/footlook/auth/logout", first);
        var second = await _host.LoginTokenAsync("a@example.com");

        Assert.Empty(await _host.GetCapturePathsAsync(second));
        using var stats = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/captures/stats", second));
        Assert.Equal(0, stats.RootElement.GetProperty("totalRequests").GetInt32());
        using var history = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/captures/history", second));
        Assert.Equal(0, history.RootElement.GetArrayLength());
        Assert.Empty(_host.Store.GetAll());

        // ...and the new session records new traffic as usual.
        await _host.GetAsync("/other");
        await _host.WaitForCaptureAsync(second, "/other");
        Assert.Equal(new[] { "/other" }, await _host.GetCapturePathsAsync(second));
    }

    // (b) ---------------------------------------------------------------------------

    [Fact]
    public async Task Traffic_recorded_before_a_session_started_is_invisible_to_it_and_later_traffic_is_visible_to_both()
    {
        // Two sessions of the SAME account, so only the session id can tell them apart.
        var s1 = await _host.LoginTokenAsync("a@example.com");
        await _host.GetAsync("/hello");
        await _host.WaitForCaptureAsync(s1, "/hello");

        var s2 = await _host.LoginTokenAsync("a@example.com");
        await _host.GetAsync("/other");
        await _host.WaitForCaptureAsync(s1, "/other");
        await _host.WaitForCaptureAsync(s2, "/other");

        Assert.Equal(new[] { "/hello", "/other" }, (await _host.GetCapturePathsAsync(s1)).OrderBy(p => p));
        Assert.Equal(new[] { "/other" }, await _host.GetCapturePathsAsync(s2));

        var before = await _host.GetCaptureIdAsync(s1, "/hello");
        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync($"/footlook/captures/{before}", s1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetAsync($"/footlook/captures/{before}", s2)).StatusCode);
    }

    // (c) ---------------------------------------------------------------------------

    [Fact]
    public async Task Logout_deletes_the_sessions_captures_from_memory_but_keeps_ones_another_session_still_observes()
    {
        var s1 = await _host.LoginTokenAsync("a@example.com");
        await _host.GetAsync("/hello"); // S1 only
        await _host.WaitForCaptureAsync(s1, "/hello");

        var s2 = await _host.LoginTokenAsync("b@example.com");
        await _host.GetAsync("/other"); // S1 and S2
        await _host.WaitForCaptureAsync(s2, "/other");
        Assert.Equal(2, _host.Store.GetAll().Count);

        Assert.Equal(HttpStatusCode.OK, (await _host.PostAsync("/footlook/auth/logout", s1)).StatusCode);

        // /hello nobody observes any more -> freed; /other survives for S2 alone.
        Assert.Equal(new[] { "/other" }, StorePaths(_host));
        Assert.Equal(new[] { "/other" }, await _host.GetCapturePathsAsync(s2));

        Assert.Equal(HttpStatusCode.OK, (await _host.PostAsync("/footlook/auth/logout", s2)).StatusCode);
        Assert.Empty(_host.Store.GetAll());
    }

    // (d) ---------------------------------------------------------------------------

    [Fact]
    public async Task Two_concurrent_sessions_of_the_same_account_are_independent()
    {
        var browser1 = await _host.LoginTokenAsync("a@example.com");
        var browser2 = await _host.LoginTokenAsync("a@example.com");
        await _host.GetAsync("/hello");
        await _host.WaitForCaptureAsync(browser1, "/hello");
        await _host.WaitForCaptureAsync(browser2, "/hello");

        // Signing out of one browser leaves the other's captures intact.
        await _host.PostAsync("/footlook/auth/logout", browser1);
        Assert.Equal(new[] { "/hello" }, StorePaths(_host));
        Assert.Equal(new[] { "/hello" }, await _host.GetCapturePathsAsync(browser2));

        // Clearing in one session does not touch the other's view either.
        var browser3 = await _host.LoginTokenAsync("a@example.com");
        await _host.GetAsync("/other");
        await _host.WaitForCaptureAsync(browser2, "/other");
        await _host.WaitForCaptureAsync(browser3, "/other");
        await _host.SendAsync(HttpMethod.Delete, "/footlook/captures", browser3);
        Assert.Empty(await _host.GetCapturePathsAsync(browser3));
        Assert.Equal(new[] { "/hello", "/other" }, (await _host.GetCapturePathsAsync(browser2)).OrderBy(p => p));

        // The last session to end frees everything.
        await _host.PostAsync("/footlook/auth/logout", browser2);
        await _host.PostAsync("/footlook/auth/logout", browser3);
        Assert.Empty(_host.Store.GetAll());
    }

    // (e) ---------------------------------------------------------------------------

    [Fact]
    public async Task Expiry_is_released_by_the_periodic_sweep_without_any_logout_or_request()
    {
        await using var host = await FootLookTestHost.StartAsync(o =>
        {
            o.TokenLifetimeHours = 0.0006; // ~2.2s
            o.SessionSweepIntervalSeconds = 1;
        });
        var token = await host.RegisterAndLoginAsync("dev@example.com");
        await host.GetAsync("/hello");
        await FootLookTestHost.WaitUntilAsync(() => Task.FromResult(host.Store.GetAll().Count == 1), "capture of /hello");

        // No request, no logout: only the sweeper can notice the session expiring.
        await FootLookTestHost.WaitUntilAsync(() => Task.FromResult(host.Store.GetAll().Count == 0), "expired session's captures released", timeoutMs: 10_000);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("/footlook/captures", token)).StatusCode);
    }

    [Fact]
    public async Task Expiry_noticed_by_a_later_request_releases_the_captures_even_with_the_sweep_off()
    {
        await using var host = await FootLookTestHost.StartAsync(o =>
        {
            o.TokenLifetimeHours = 0.0006; // ~2.2s
            o.SessionSweepIntervalSeconds = 3600;
        });
        await host.RegisterAndLoginAsync("dev@example.com");
        await host.GetAsync("/hello");
        await FootLookTestHost.WaitUntilAsync(() => Task.FromResult(host.Store.GetAll().Count == 1), "capture of /hello");

        await Task.Delay(2600);
        Assert.Single(host.Store.GetAll()); // nobody has noticed the expiry yet

        await host.GetAsync("/other"); // ShadowMiddleware finds the session expired
        Assert.Empty(host.Store.GetAll());
    }

    // (f) ---------------------------------------------------------------------------

    [Fact]
    public async Task An_ended_sessions_old_token_gets_401_and_the_live_hub_rejects_it_even_after_the_same_account_signs_in_again()
    {
        var old = await _host.LoginTokenAsync("a@example.com");
        await _host.PostAsync("/footlook/auth/logout", old);
        var fresh = await _host.LoginTokenAsync("a@example.com");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/captures", old)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/captures/stats", old)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.SendAsync(HttpMethod.Delete, "/footlook/captures", old)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.PostAsync($"/footlook/live/negotiate?negotiateVersion=1&footlook_token={old}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync("/footlook/captures", fresh)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.PostAsync($"/footlook/live/negotiate?negotiateVersion=1&footlook_token={fresh}")).StatusCode);
    }

    // (g) ---------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-live-session")]
    public async Task A_correctly_signed_token_with_a_missing_empty_or_unknown_session_id_matches_nothing(string? sessionId)
    {
        var real = await _host.LoginTokenAsync("a@example.com");
        await _host.GetAsync("/hello");
        await _host.WaitForCaptureAsync(real, "/hello");

        var forged = Forge("some-user", sessionId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/captures", forged)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/captures/history", forged)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.SendAsync(HttpMethod.Delete, "/footlook/captures", forged)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.PostAsync($"/footlook/live/negotiate?negotiateVersion=1&footlook_token={forged}")).StatusCode);

        // The real session's captures were neither shown nor deleted.
        Assert.Equal(new[] { "/hello" }, await _host.GetCapturePathsAsync(real));
    }

    private string Forge(string userId, string? sessionId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(FootLookAuthDefaults.AdminClaimType, "false"),
        };
        if (sessionId is not null)
        {
            claims.Add(new Claim(FootLookAuthDefaults.SessionIdClaimType, sessionId));
        }

        var key = _host.Services.GetRequiredService<FootLookTokenService>().SigningKey;
        var token = new JwtSecurityToken(
            FootLookAuthDefaults.Issuer,
            FootLookAuthDefaults.Audience,
            claims,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddHours(1),
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // (h) ---------------------------------------------------------------------------

    [Fact]
    public async Task No_capture_is_recorded_while_no_session_is_live()
    {
        var first = await _host.LoginTokenAsync("a@example.com");
        await _host.PostAsync("/footlook/auth/logout", first);

        await _host.GetAsync("/nobody-watching"); // no live session -> not queued at all

        var second = await _host.LoginTokenAsync("b@example.com");
        await _host.GetAsync("/sentinel");
        await _host.WaitForCaptureAsync(second, "/sentinel");

        Assert.Equal(new[] { "/sentinel" }, StorePaths(_host));
    }
}
