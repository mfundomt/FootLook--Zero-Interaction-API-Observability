using System.Net;
using System.Net.Http.Headers;

namespace FootLook.Tests;

/// <summary>
/// The dashboard ships inside FootLook.Core: a host that only calls AddFootLook, UseFootLook and
/// MapFootLookEndpoints (no wwwroot, no UseStaticFiles) must still serve it.
/// </summary>
public class DashboardTests
{
    [Theory]
    [InlineData("/footlook.html", "text/html", "var AUTH_BASE = '/footlook';")]
    [InlineData("/footlook-login.html", "text/html", "var DASHBOARD_URL = '/footlook.html';")]
    [InlineData("/footlook-connect.js", "text/javascript", "FootLookConnect")]
    public async Task Dashboard_files_are_served_from_the_package(string path, string mediaType, string marker)
    {
        await using var host = await FootLookTestHost.StartAsync();

        var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Contains(marker, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unchanged_dashboard_is_revalidated_with_a_304()
    {
        await using var host = await FootLookTestHost.StartAsync();

        var first = await host.GetAsync("/footlook.html");
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        var request = new HttpRequestMessage(HttpMethod.Get, "/footlook.html");
        request.Headers.IfNoneMatch.Add(etag!);
        var second = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());

        var stale = new HttpRequestMessage(HttpMethod.Get, "/footlook.html");
        stale.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"stale\""));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task Dashboard_requests_are_not_captured()
    {
        await using var host = await FootLookTestHost.StartAsync();
        var token = await host.RegisterAndLoginAsync("dev@example.com");

        await host.GetAsync("/footlook.html");
        await host.GetAsync("/footlook-login.html");
        await host.GetAsync("/footlook-connect.js");
        await host.GetAsync("/hello");
        await host.WaitForCaptureAsync(token, "/hello");

        var paths = await host.GetCapturePathsAsync(token);
        Assert.DoesNotContain(paths, p => p.StartsWith("/footlook", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Dashboard_can_be_turned_off()
    {
        await using var host = await FootLookTestHost.StartAsync(o => o.EnableDashboard = false);

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/footlook.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/footlook-login.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/footlook-connect.js")).StatusCode);
        // The API itself is unaffected.
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/footlook/health")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_calls_a_custom_endpoint_base_path()
    {
        await using var host = await FootLookTestHost.StartAsync(o => o.EndpointBasePath = "/observe");

        var dashboard = await (await host.GetAsync("/footlook.html")).Content.ReadAsStringAsync();
        var login = await (await host.GetAsync("/footlook-login.html")).Content.ReadAsStringAsync();

        Assert.Contains("var AUTH_BASE = '/observe';", dashboard);
        Assert.Contains("'/observe/captures/stats'", dashboard);
        Assert.Contains("'/observe/live'", dashboard);
        Assert.DoesNotContain("'/footlook/", dashboard);
        Assert.DoesNotContain("'/footlook'", dashboard);

        Assert.Contains("'/observe/auth/login'", login);
        Assert.DoesNotContain("'/footlook/", login);
        // Page URLs are not API paths and stay where they are.
        Assert.Contains("var DASHBOARD_URL = '/footlook.html';", login);
    }
}
