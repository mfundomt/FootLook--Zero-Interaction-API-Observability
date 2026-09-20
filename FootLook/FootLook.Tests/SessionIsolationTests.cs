using System.Net;
using System.Text.Json;

namespace FootLook.Tests;

/// <summary>A session only ever sees traffic captured while it was open; A and B are two sessions (here of two accounts).</summary>
public class SessionIsolationTests : IAsyncLifetime
{
    private FootLookTestHost _host = null!;
    private string _tokenA = null!;
    private string _tokenB = null!;

    public async Task InitializeAsync()
    {
        _host = await FootLookTestHost.StartAsync();
        Assert.Equal(HttpStatusCode.Created, (await _host.RegisterAsync("a@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _host.RegisterAsync("b@example.com")).StatusCode);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>A logs in, traffic /a-only happens; then B logs in, traffic /both happens. Returns once A sees both.</summary>
    private async Task ArrangeAsync()
    {
        _tokenA = await _host.LoginTokenAsync("a@example.com");
        await _host.GetAsync("/hello"); // seen by A only
        await _host.WaitForCaptureAsync(_tokenA, "/hello");

        _tokenB = await _host.LoginTokenAsync("b@example.com");
        await _host.GetAsync("/other"); // seen by A and B
        await _host.WaitForCaptureAsync(_tokenA, "/other");
        await _host.WaitForCaptureAsync(_tokenB, "/other");
    }

    [Fact]
    public async Task Traffic_captured_while_only_A_was_logged_in_is_invisible_to_Bs_session()
    {
        await ArrangeAsync();

        var aPaths = await _host.GetCapturePathsAsync(_tokenA);
        var bPaths = await _host.GetCapturePathsAsync(_tokenB);

        Assert.Contains("/hello", aPaths);
        Assert.Contains("/other", aPaths);
        Assert.DoesNotContain("/hello", bPaths);
        Assert.Contains("/other", bPaths);
    }

    [Fact]
    public async Task Capture_by_id_is_404_for_a_session_that_did_not_observe_it()
    {
        await ArrangeAsync();
        var aOnly = await _host.GetCaptureIdAsync(_tokenA, "/hello");
        var shared = await _host.GetCaptureIdAsync(_tokenA, "/other");
        Assert.NotNull(aOnly);
        Assert.NotNull(shared);

        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync($"/footlook/captures/{aOnly}", _tokenA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetAsync($"/footlook/captures/{aOnly}", _tokenB)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync($"/footlook/captures/{shared}", _tokenA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync($"/footlook/captures/{shared}", _tokenB)).StatusCode);
    }

    [Fact]
    public async Task Unknown_capture_id_and_other_sessions_capture_id_look_identical()
    {
        await ArrangeAsync();
        var aOnly = await _host.GetCaptureIdAsync(_tokenA, "/hello");

        var notYours = await _host.GetAsync($"/footlook/captures/{aOnly}", _tokenB);
        var missing = await _host.GetAsync($"/footlook/captures/{Guid.NewGuid()}", _tokenB);

        Assert.Equal(missing.StatusCode, notYours.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await notYours.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Every_capture_read_route_is_scoped_to_the_callers_session()
    {
        await ArrangeAsync();

        // B only observed /other.
        using var bList = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/captures", _tokenB));
        Assert.Equal(1, bList.RootElement.GetProperty("total").GetInt32());

        using var bStats = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/captures/stats", _tokenB));
        Assert.Equal(1, bStats.RootElement.GetProperty("totalRequests").GetInt32());

        using var bRecent = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/captures/recent", _tokenB));
        Assert.Equal(1, bRecent.RootElement.GetArrayLength());

        using var bRecentPaged = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/captures/recent?page=1&pageSize=10", _tokenB));
        Assert.Equal(1, bRecentPaged.RootElement.GetProperty("totalCount").GetInt32());

        using var bHistory = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/captures/history", _tokenB));
        Assert.Equal(1, bHistory.RootElement.GetArrayLength());

        using var bIdentity = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync(
            "/footlook/captures/identity?footlookSessionId=s&footlookTabId=t&minConfidence=0", _tokenB));
        Assert.True(bIdentity.RootElement.GetProperty("totalCount").GetInt32() <= 1);

        // A observed both.
        using var aStats = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/captures/stats", _tokenA));
        Assert.Equal(2, aStats.RootElement.GetProperty("totalRequests").GetInt32());
    }

    [Fact]
    public async Task Traffic_after_both_are_logged_in_is_visible_to_both()
    {
        await ArrangeAsync();

        await _host.GetAsync("/hello");
        await FootLookTestHost.WaitUntilAsync(async () =>
            (await _host.GetCapturePathsAsync(_tokenA)).Count(p => p == "/hello") == 2 &&
            (await _host.GetCapturePathsAsync(_tokenB)).Count(p => p == "/hello") == 1,
            "second /hello visible to A (twice) and B (once)");
    }

    [Fact]
    public async Task Deleting_captures_as_A_leaves_Bs_copy()
    {
        await ArrangeAsync();

        var clear = await _host.SendAsync(HttpMethod.Delete, "/footlook/captures", _tokenA);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

        Assert.Empty(await _host.GetCapturePathsAsync(_tokenA));
        Assert.Equal(new[] { "/other" }, await _host.GetCapturePathsAsync(_tokenB));

        // And B's copy is still retrievable by id.
        var id = await _host.GetCaptureIdAsync(_tokenB, "/other");
        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync($"/footlook/captures/{id}", _tokenB)).StatusCode);
    }

    [Fact]
    public async Task Deleting_captures_as_B_does_not_touch_As_private_captures()
    {
        await ArrangeAsync();

        await _host.SendAsync(HttpMethod.Delete, "/footlook/captures", _tokenB);

        Assert.Empty(await _host.GetCapturePathsAsync(_tokenB));
        var aPaths = await _host.GetCapturePathsAsync(_tokenA);
        Assert.Contains("/hello", aPaths);
        Assert.Contains("/other", aPaths);
    }

    [Fact]
    public async Task Clearing_after_a_partner_cleared_still_works_and_removes_the_capture_entirely()
    {
        await ArrangeAsync();

        await _host.SendAsync(HttpMethod.Delete, "/footlook/captures", _tokenA);
        await _host.SendAsync(HttpMethod.Delete, "/footlook/captures", _tokenB);

        var store = (FootLook.Core.Interfaces.IShadowCaptureStore)_host.Services.GetService(typeof(FootLook.Core.Interfaces.IShadowCaptureStore))!;
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public async Task Logging_out_A_does_not_stop_capture_for_B_and_A_signing_back_in_sees_nothing_old()
    {
        await ArrangeAsync();
        await _host.PostAsync("/footlook/auth/logout", _tokenA);

        await _host.GetAsync("/hello");
        await FootLookTestHost.WaitUntilAsync(async () => (await _host.GetCapturePathsAsync(_tokenB)).Count(p => p == "/hello") == 1, "B sees the new /hello");

        // A logs in again: a new session, so nothing from A's first session and nothing
        // from while A was signed out.
        var a2 = await _host.LoginTokenAsync("a@example.com");
        Assert.Empty(await _host.GetCapturePathsAsync(a2));

        // B is unaffected: it keeps the traffic that overlapped its session.
        var bPaths = await _host.GetCapturePathsAsync(_tokenB);
        Assert.Contains("/other", bPaths);
        Assert.Contains("/hello", bPaths);
    }

    [Fact]
    public async Task Same_account_logged_in_twice_has_one_copy_of_each_capture_per_session()
    {
        var t1 = await _host.LoginTokenAsync("a@example.com");
        var t2 = await _host.LoginTokenAsync("a@example.com");

        await _host.GetAsync("/hello");
        await _host.WaitForCaptureAsync(t1, "/hello");

        Assert.Single(await _host.GetCapturePathsAsync(t1));
        Assert.Single(await _host.GetCapturePathsAsync(t2));
    }

    [Fact]
    public async Task Json_responses_never_expose_observer_session_ids_or_other_accounts_ids()
    {
        await ArrangeAsync();
        using var meB = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/auth/me", _tokenB));
        using var meA = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/auth/me", _tokenA));
        var idA = meA.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        var idB = meB.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        var sidA = meA.RootElement.GetProperty("sessionId").GetString()!;
        var sidB = meB.RootElement.GetProperty("sessionId").GetString()!;
        var shared = await _host.GetCaptureIdAsync(_tokenB, "/other");

        foreach (var route in new[]
                 {
                     "/footlook/captures?pageSize=100",
                     $"/footlook/captures/{shared}",
                     "/footlook/captures/recent",
                     "/footlook/captures/history",
                 })
        {
            var raw = await (await _host.GetAsync(route, _tokenB)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("observerIds", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("observerSessionIds", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sidA, raw);
            Assert.DoesNotContain(sidB, raw);
            Assert.DoesNotContain(idA, raw);
            Assert.DoesNotContain(idB, raw);
        }
    }
}
