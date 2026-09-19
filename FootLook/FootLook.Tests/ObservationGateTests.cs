using System.Net;
using System.Text.Json;

namespace FootLook.Tests;

/// <summary>Capture only runs while somebody is logged in; and only what happened during that session is visible.</summary>
public class ObservationGateTests : IAsyncLifetime
{
    private FootLookTestHost _host = null!;

    public async Task InitializeAsync() => _host = await FootLookTestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Nothing_is_captured_before_login_and_capture_starts_after_login()
    {
        await _host.RegisterAsync("dev@example.com"); // registering is not logging in

        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync("/hello")).StatusCode);

        var token = await _host.LoginTokenAsync("dev@example.com");
        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync("/other")).StatusCode);

        // Captures go through an async queue in FIFO order, so once /other is visible any
        // capture of /hello (which was enqueued earlier, if at all) would be visible too.
        await _host.WaitForCaptureAsync(token, "/other");

        var paths = await _host.GetCapturePathsAsync(token);
        Assert.DoesNotContain("/hello", paths);
        Assert.Contains("/other", paths);
    }

    [Fact]
    public async Task Nothing_is_captured_after_logout()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");
        await _host.GetAsync("/hello");
        await _host.WaitForCaptureAsync(token, "/hello");

        await _host.PostAsync("/footlook/auth/logout", token);
        await _host.GetAsync("/after-logout");

        // Log back in and capture a sentinel; anything enqueued before it would be visible by now.
        var second = await _host.LoginTokenAsync("dev@example.com");
        await _host.GetAsync("/other");
        await _host.WaitForCaptureAsync(second, "/other");

        var paths = await _host.GetCapturePathsAsync(second);
        Assert.DoesNotContain("/after-logout", paths);
        Assert.Contains("/hello", paths); // captured during the earlier session, still owned by this account
    }

    [Fact]
    public async Task Expired_session_stops_capture()
    {
        await using var shortLived = await FootLookTestHost.StartAsync(o => o.TokenLifetimeHours = 0.0002); // ~0.7s
        await shortLived.RegisterAsync("dev@example.com");
        await shortLived.LoginTokenAsync("dev@example.com");

        await Task.Delay(1000);
        await shortLived.GetAsync("/hello"); // the only session has expired -> nobody observing -> not queued

        await shortLived.LoginTokenAsync("dev@example.com"); // fresh session, live for ~0.7s
        await shortLived.GetAsync("/other");

        // Read the store directly: the short-lived token itself may already have lapsed.
        var store = Store(shortLived);
        await FootLookTestHost.WaitUntilAsync(() => Task.FromResult(store.GetAll().Any(c => c.Path == "/other")), "capture of /other");
        Assert.DoesNotContain(store.GetAll(), c => c.Path == "/hello");
    }

    private static FootLook.Core.Interfaces.IShadowCaptureStore Store(FootLookTestHost host) =>
        (FootLook.Core.Interfaces.IShadowCaptureStore)host.Services.GetService(typeof(FootLook.Core.Interfaces.IShadowCaptureStore))!;

    [Fact]
    public async Task Captures_record_the_request_details()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");

        await _host.GetAsync("/hello");
        await _host.WaitForCaptureAsync(token, "/hello");

        using var doc = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/captures?pathContains=hello", token));
        var capture = doc.RootElement.GetProperty("results")[0];
        Assert.Equal("GET", capture.GetProperty("method").GetString());
        Assert.Equal("/hello", capture.GetProperty("path").GetString());
        Assert.Equal(200, capture.GetProperty("statusCode").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task FootLooks_own_routes_are_never_captured()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");

        // Exercise a spread of FootLook routes while a session is active.
        await _host.GetAsync("/footlook/health");
        await _host.GetAsync("/footlook/auth/me", token);
        await _host.GetAsync("/footlook/captures/stats", token);
        await _host.GetAsync("/footlook/captures", token);
        await _host.LoginAsync("dev@example.com");
        await _host.LoginAsync("dev@example.com", "wrong-password");
        await _host.RegisterAsync("someone.else@example.com");

        await _host.GetAsync("/hello");
        await _host.WaitForCaptureAsync(token, "/hello");

        var store = Store(_host);
        Assert.All(store.GetAll(), c => Assert.DoesNotContain("/footlook", c.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(store.GetAll(), c => c.Path == "/hello");
    }

    [Fact]
    public async Task Passwords_from_login_and_register_never_appear_in_any_capture()
    {
        const string secret = "S3cr3t-Login-Passw0rd-UNIQUE";
        var token = await _host.RegisterAndLoginAsync("dev@example.com", secret);
        await _host.LoginAsync("dev@example.com", secret);
        await _host.LoginAsync("dev@example.com", secret + "-wrong");
        await _host.RegisterAsync("other@example.com", secret);

        await _host.GetAsync("/hello");
        await _host.WaitForCaptureAsync(token, "/hello");

        var store = Store(_host);
        var serialized = JsonSerializer.Serialize(store.GetAll());
        Assert.DoesNotContain("S3cr3t-Login-Passw0rd", serialized);

        var apiView = await (await _host.GetAsync("/footlook/captures?pageSize=100", token)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("S3cr3t-Login-Passw0rd", apiView);
    }

    [Fact]
    public async Task Bearer_tokens_are_not_stored_in_captured_headers()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");

        // A host endpoint called with the FootLook bearer attached.
        await _host.GetAsync("/hello", token);
        await _host.WaitForCaptureAsync(token, "/hello");

        var store = Store(_host);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(store.GetAll()));
    }

    [Fact]
    public async Task Sensitive_fields_in_host_request_bodies_are_masked()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");

        var response = await _host.SendAsync(HttpMethod.Post, "/echo", body: new { user = "bob", password = "Hunter2-UNIQUE-value" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _host.WaitForCaptureAsync(token, "/echo");

        var raw = await (await _host.GetAsync("/footlook/captures?pathContains=echo", token)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Hunter2-UNIQUE-value", raw);
        Assert.Contains("bob", raw);
    }

    [Fact]
    public async Task Capture_responses_never_expose_observer_ids_or_user_ids()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");
        using var me = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/auth/me", token));
        var userId = me.RootElement.GetProperty("user").GetProperty("id").GetString()!;

        await _host.GetAsync("/hello");
        await _host.WaitForCaptureAsync(token, "/hello");
        var id = await _host.GetCaptureIdAsync(token, "/hello");

        foreach (var route in new[]
                 {
                     "/footlook/captures?pageSize=100",
                     $"/footlook/captures/{id}",
                     "/footlook/captures/recent",
                     "/footlook/captures/recent?page=1&pageSize=5",
                     "/footlook/captures/history",
                     "/footlook/captures/identity?footlookSessionId=s&footlookTabId=t&minConfidence=0",
                     "/footlook/captures/stats",
                 })
        {
            var raw = await (await _host.GetAsync(route, token)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("observerIds", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(userId, raw);
        }

        // The plain capture list carries no account id either.
        var listRaw = await (await _host.GetAsync("/footlook/captures?pageSize=100", token)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(userId, listRaw);
    }

    [Fact]
    public async Task Pagination_and_filters_apply_only_to_the_callers_captures()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");
        await _host.GetAsync("/hello");
        await _host.GetAsync("/other");
        await _host.GetAsync("/missing"); // 404 from the host
        await _host.WaitForCaptureAsync(token, "/missing");
        await _host.WaitForCaptureAsync(token, "/hello");
        await _host.WaitForCaptureAsync(token, "/other");

        using var failed = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/captures?failedOnly=true", token));
        Assert.Equal(1, failed.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("/missing", failed.RootElement.GetProperty("results")[0].GetProperty("path").GetString());

        using var paged = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/captures?pageSize=2&page=2&sortDirection=asc", token));
        Assert.Equal(3, paged.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, paged.RootElement.GetProperty("results").GetArrayLength());

        using var stats = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/captures/stats", token));
        Assert.Equal(3, stats.RootElement.GetProperty("totalRequests").GetInt32());
        Assert.Equal(1, stats.RootElement.GetProperty("failedRequests").GetInt32());
    }

    [Fact]
    public async Task Distinct_requests_are_not_collapsed_by_deduplication()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");

        for (var i = 0; i < 5; i++)
        {
            await _host.GetAsync("/hello");
        }

        await FootLookTestHost.WaitUntilAsync(async () => (await _host.GetCapturePathsAsync(token)).Count(p => p == "/hello") == 5, "five captures of /hello");
    }

    [Fact]
    public async Task Requests_sharing_a_correlation_id_are_deduplicated()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");

        for (var i = 0; i < 2; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/hello");
            request.Headers.Add("X-Correlation-ID", "same-correlation-id");
            await _host.Client.SendAsync(request);
        }

        await _host.GetAsync("/other");
        await _host.WaitForCaptureAsync(token, "/other");

        Assert.Single((await _host.GetCapturePathsAsync(token)), p => p == "/hello");
    }

    [Fact]
    public async Task Ignored_paths_option_is_respected()
    {
        await using var host = await FootLookTestHost.StartAsync(o => o.IgnoredPaths.Add("/hello"));
        var token = await host.RegisterAndLoginAsync("dev@example.com");

        await host.GetAsync("/hello");
        await host.GetAsync("/other");
        await host.WaitForCaptureAsync(token, "/other");

        Assert.DoesNotContain("/hello", await host.GetCapturePathsAsync(token));
    }

    [Fact]
    public async Task Post_body_of_json_content_is_captured_for_host_endpoints()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");

        await _host.SendAsync(HttpMethod.Post, "/echo", body: new { note = "visible-body-text" });
        await _host.WaitForCaptureAsync(token, "/echo");

        var raw = await (await _host.GetAsync("/footlook/captures?pathContains=echo", token)).Content.ReadAsStringAsync();
        Assert.Contains("visible-body-text", raw);
    }
}
