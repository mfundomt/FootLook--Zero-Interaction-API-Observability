using System.Net;
using System.Text.Json;
using FootLook.Core.Models;

namespace FootLook.Central.Tests;

public class ConnectEndpointTests : IDisposable
{
    private const string Dashboard = "https://good.example/app/footlook.html";

    private readonly CentralFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> ConnectAsync(FootLookUser user, string projectId, string? returnUrl) =>
        await _factory.PostAsync($"/projects/{projectId}/connect", await _factory.SessionAsync(user), new { returnUrl });

    private async Task<(FootLookUser Owner, string ProjectId)> ProjectAsync(params string[] urls)
    {
        var owner = _factory.NewUser("owner");
        return (owner, await _factory.CreateProjectAsync(owner, "Connect me", urls.Length == 0 ? new[] { Dashboard } : urls));
    }

    // ---- success ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_gets_a_pass_for_the_project_with_the_allowed_return_url_echoed_back()
    {
        var (owner, id) = await ProjectAsync();

        var response = await ConnectAsync(owner, id, Dashboard);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await CentralFactory.JsonAsync(response);
        Assert.Equal(new[] { "pass", "expiresAtUtc", "returnUrl" }, doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(Dashboard, doc.RootElement.GetProperty("returnUrl").GetString());
        Assert.EndsWith("Z", doc.RootElement.GetProperty("expiresAtUtc").GetString());
        Assert.InRange((doc.RootElement.GetProperty("expiresAtUtc").GetDateTime() - DateTime.UtcNow).TotalMinutes, 4.5, 5.1);

        var pass = doc.RootElement.GetProperty("pass").GetString()!;
        var p = HostEmulator.Payload(pass);
        Assert.Equal(id, p.GetProperty("aud").GetString());
        Assert.Equal("owner", p.GetProperty("role").GetString());
        Assert.Equal(owner.Id, p.GetProperty("sub").GetString());
        Assert.Equal("owner@example.invalid", p.GetProperty("email").GetString());
        Assert.Equal("owner", p.GetProperty("name").GetString());
        Assert.Equal(CentralFactory.Issuer, p.GetProperty("iss").GetString());

        // A host that knows this project id accepts it; a host for another project does not.
        var jwks = await HostEmulator.JwksAsync(_factory);
        Assert.True((await HostEmulator.ValidateAsync(pass, jwks, CentralFactory.Issuer, id)).IsValid);
        Assert.False((await HostEmulator.ValidateAsync(pass, jwks, CentralFactory.Issuer, "prj_bbbbbbbbbbbbbbbb")).IsValid);
    }

    [Fact]
    public async Task A_member_gets_a_pass_with_the_member_role()
    {
        var (owner, id) = await ProjectAsync();
        var member = _factory.NewUser("tester");
        var (_, code) = await _factory.CreateInviteAsync(owner, id);
        await _factory.PostAsync("/invites/redeem", await _factory.SessionAsync(member), new { code });

        using var doc = await CentralFactory.JsonAsync(await ConnectAsync(member, id, Dashboard));

        Assert.Equal("member", HostEmulator.Payload(doc.RootElement.GetProperty("pass").GetString()!).GetProperty("role").GetString());
    }

    [Fact]
    public async Task The_pass_carries_the_current_name_and_email_not_the_stale_ones_in_the_session_token()
    {
        var (owner, id) = await ProjectAsync();
        var token = await _factory.SessionAsync(owner);
        _factory.Accounts.Rename(owner.Id, "Owner Renamed");

        using var doc = await CentralFactory.JsonAsync(await _factory.PostAsync($"/projects/{id}/connect", token, new { returnUrl = Dashboard }));

        Assert.Equal("Owner Renamed", HostEmulator.Payload(doc.RootElement.GetProperty("pass").GetString()!).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Every_connect_gives_a_fresh_pass()
    {
        var (owner, id) = await ProjectAsync();

        using var a = await CentralFactory.JsonAsync(await ConnectAsync(owner, id, Dashboard));
        using var b = await CentralFactory.JsonAsync(await ConnectAsync(owner, id, Dashboard));

        Assert.NotEqual(a.RootElement.GetProperty("pass").GetString(), b.RootElement.GetProperty("pass").GetString());
    }

    // ---- authorization: the 404 is the decision -----------------------------------------------------------------

    [Fact]
    public async Task A_non_member_gets_404_project_not_found_and_no_pass()
    {
        var (_, id) = await ProjectAsync();
        var stranger = _factory.NewUser();

        var response = await ConnectAsync(stranger, id, Dashboard);

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.NotFound, "project_not_found");
        Assert.DoesNotContain("pass", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_non_member_learns_nothing_the_answer_is_the_same_for_a_bad_return_url_a_bad_body_and_a_missing_project()
    {
        var (_, id) = await ProjectAsync();
        var stranger = _factory.NewUser();
        var token = await _factory.SessionAsync(stranger);

        var responses = new[]
        {
            await ConnectAsync(stranger, id, Dashboard),
            await ConnectAsync(stranger, id, "https://evil.example/"),
            await ConnectAsync(stranger, id, null),
            await _factory.SendAsync(HttpMethod.Post, $"/projects/{id}/connect", token, rawBody: "{not json"),
            await _factory.SendAsync(HttpMethod.Post, $"/projects/{id}/connect", token),
            await ConnectAsync(stranger, "prj_zzzzzzzzzzzzzzzz", Dashboard),
            await ConnectAsync(stranger, "prj_zzzzzzzzzzzzzzzz", "https://evil.example/"),
        };

        foreach (var response in responses)
        {
            await CentralFactory.AssertErrorAsync(response, HttpStatusCode.NotFound, "project_not_found");
        }
    }

    [Fact]
    public async Task Owning_another_project_gives_no_access_to_this_one()
    {
        var (_, id) = await ProjectAsync();
        var other = _factory.NewUser();
        await _factory.CreateProjectAsync(other);

        await CentralFactory.AssertErrorAsync(await ConnectAsync(other, id, Dashboard), HttpStatusCode.NotFound, "project_not_found");
    }

    [Fact]
    public async Task A_removed_member_and_a_deleted_project_can_no_longer_connect()
    {
        var (owner, id) = await ProjectAsync();
        var member = _factory.NewUser();
        var (_, code) = await _factory.CreateInviteAsync(owner, id);
        await _factory.PostAsync("/invites/redeem", await _factory.SessionAsync(member), new { code });
        Assert.Equal(HttpStatusCode.OK, (await ConnectAsync(member, id, Dashboard)).StatusCode);

        await _factory.DeleteAsync($"/projects/{id}/members/{member.Id}", await _factory.SessionAsync(owner));
        await CentralFactory.AssertErrorAsync(await ConnectAsync(member, id, Dashboard), HttpStatusCode.NotFound, "project_not_found");

        await _factory.DeleteAsync($"/projects/{id}", await _factory.SessionAsync(owner));
        await CentralFactory.AssertErrorAsync(await ConnectAsync(owner, id, Dashboard), HttpStatusCode.NotFound, "project_not_found");
    }

    [Fact]
    public async Task A_user_who_no_longer_exists_cannot_get_a_pass()
    {
        var (owner, id) = await ProjectAsync();
        var token = await _factory.SessionAsync(owner);
        _factory.Accounts.Remove(owner.Id);

        var response = await _factory.PostAsync($"/projects/{id}/connect", token, new { returnUrl = Dashboard });

        // The membership row still exists in the fake, but the account is gone: no pass.
        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    // ---- returnUrl rules ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Dashboard)]
    [InlineData("https://GOOD.example/app/footlook.html")]
    [InlineData("https://good.example/app/footlook.html/")]
    [InlineData("https://good.example/app/footlook.html?next=1&x=%20y")]
    [InlineData("https://good.example/app/footlook.html#old=1")]
    public async Task A_url_that_matches_an_allowed_one_is_accepted_and_what_comes_back_is_the_stored_url(string requested)
    {
        var (owner, id) = await ProjectAsync();

        using var doc = await CentralFactory.JsonAsync(await ConnectAsync(owner, id, requested));

        Assert.Equal(Dashboard, doc.RootElement.GetProperty("returnUrl").GetString());
    }

    [Theory]
    [InlineData("http://localhost:5103/footlook.html", "http://localhost:5103/footlook.html")]
    [InlineData("http://127.0.0.1:4200/any/path?x=1#frag", "http://127.0.0.1:4200/any/path")]
    public async Task Localhost_is_allowed_for_every_project_with_any_port_and_path(string requested, string echoed)
    {
        var (owner, id) = await ProjectAsync();

        using var doc = await CentralFactory.JsonAsync(await ConnectAsync(owner, id, requested));

        Assert.Equal(echoed, doc.RootElement.GetProperty("returnUrl").GetString());
    }

    [Fact]
    public async Task Localhost_works_even_for_a_project_with_no_allowed_urls()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);

        Assert.Equal(HttpStatusCode.OK, (await ConnectAsync(owner, id, "http://localhost:5103/footlook.html")).StatusCode);
        await CentralFactory.AssertErrorAsync(await ConnectAsync(owner, id, Dashboard), HttpStatusCode.BadRequest, "invalid_return_url");
    }

    [Theory]
    [InlineData("https://evil.example/app/footlook.html")]
    [InlineData("https://good.example@evil.example/app/footlook.html")]
    [InlineData("https://evil.example@good.example/app/footlook.html")]
    [InlineData("https://good.example:8443/app/footlook.html")]
    [InlineData("http://good.example/app/footlook.html")]
    [InlineData("https://good.example./app/footlook.html")]
    [InlineData("https://good.example/app/../admin")]
    [InlineData("https://good.example/app/%2e%2e/footlook.html")]
    [InlineData("https://good.example/app/footlook.html/extra")]
    [InlineData("https://good.example/other")]
    [InlineData("//evil.example")]
    [InlineData("//good.example/app/footlook.html")]
    [InlineData("/app/footlook.html")]
    [InlineData("javascript:alert(document.domain)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("https://good.example.evil.example/app/footlook.html")]
    [InlineData("http://localhost.evil.example:4200/x")]
    [InlineData("http://localhost@evil.example/x")]
    [InlineData("https://localhost:4200/x")]
    [InlineData("")]
    [InlineData("not a url")]
    public async Task Anything_else_is_400_invalid_return_url_and_no_pass_is_issued(string requested)
    {
        var (owner, id) = await ProjectAsync();

        var response = await ConnectAsync(owner, id, requested);

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_return_url");
        Assert.DoesNotContain("pass", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_very_long_url_is_refused()
    {
        var (owner, id) = await ProjectAsync();

        await CentralFactory.AssertErrorAsync(await ConnectAsync(owner, id, Dashboard + "?" + new string('a', 3000)), HttpStatusCode.BadRequest, "invalid_return_url");
        await CentralFactory.AssertErrorAsync(await ConnectAsync(owner, id, "https://good.example/" + new string('a', 100_000)), HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_url_on_one_projects_list_is_not_allowed_for_another_project()
    {
        var (_, first) = await ProjectAsync("https://one.example/x");
        var owner2 = _factory.NewUser();
        var second = await _factory.CreateProjectAsync(owner2, "Two", "https://two.example/y");

        await CentralFactory.AssertErrorAsync(await ConnectAsync(owner2, second, "https://one.example/x"), HttpStatusCode.BadRequest, "invalid_return_url");
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"returnUrl\":null}")]
    [InlineData("{\"returnUrl\":5}")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("")]
    public async Task A_member_with_an_unusable_body_gets_400_invalid_request(string body)
    {
        var (owner, id) = await ProjectAsync();

        var response = await _factory.SendAsync(HttpMethod.Post, $"/projects/{id}/connect", await _factory.SessionAsync(owner), rawBody: body.Length == 0 ? null : body);

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task After_the_owner_removes_an_allowed_url_it_stops_working()
    {
        var (owner, id) = await ProjectAsync();
        Assert.Equal(HttpStatusCode.OK, (await ConnectAsync(owner, id, Dashboard)).StatusCode);

        await _factory.PatchAsync($"/projects/{id}", await _factory.SessionAsync(owner), new { allowedReturnUrls = new[] { "https://other.example/x" } });

        await CentralFactory.AssertErrorAsync(await ConnectAsync(owner, id, Dashboard), HttpStatusCode.BadRequest, "invalid_return_url");
    }

    [Fact]
    public async Task Connect_is_rate_limited_per_user()
    {
        using var limited = new CentralFactory(f => f.Settings["Central:RateLimits:ConnectPerMinute"] = "2");
        var a = limited.NewUser();
        var b = limited.NewUser();
        var idA = await limited.CreateProjectAsync(a);
        var idB = await limited.CreateProjectAsync(b);
        var tokenA = await limited.SessionAsync(a);
        var body = new { returnUrl = "http://localhost:4200/x" };

        Assert.Equal(HttpStatusCode.OK, (await limited.PostAsync($"/projects/{idA}/connect", tokenA, body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await limited.PostAsync($"/projects/{idA}/connect", tokenA, body)).StatusCode);
        var over = await limited.PostAsync($"/projects/{idA}/connect", tokenA, body);
        await CentralFactory.AssertErrorAsync(over, HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.True(over.Headers.Contains("Retry-After"));

        Assert.Equal(HttpStatusCode.OK, (await limited.PostAsync($"/projects/{idB}/connect", await limited.SessionAsync(b), body)).StatusCode);
    }
}

public class CorsAndSignInRateLimitTests : IDisposable
{
    private readonly CentralFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> PreflightAsync(string path, string origin, string method = "POST", string headers = "authorization,content-type")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return await _factory.CreateClient().SendAsync(request);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Theory]
    [InlineData("https://www.footlook.co.za")]
    [InlineData("https://footlook.co.za")]
    [InlineData("http://localhost:4200")]
    [InlineData("http://localhost:4210")]
    public async Task Preflight_from_an_allowed_origin_is_answered_for_every_route_family(string origin)
    {
        foreach (var (path, method) in new[]
                 {
                     ("/auth/microsoft", "POST"), ("/me", "GET"), ("/projects", "POST"), ("/projects", "GET"),
                     ("/projects/prj_aaaaaaaaaaaaaaaa", "PATCH"), ("/projects/prj_aaaaaaaaaaaaaaaa", "DELETE"),
                     ("/projects/prj_aaaaaaaaaaaaaaaa/members/abc", "DELETE"), ("/projects/prj_aaaaaaaaaaaaaaaa/invites", "POST"),
                     ("/projects/prj_aaaaaaaaaaaaaaaa/connect", "POST"), ("/invites/redeem", "POST"),
                 })
        {
            var response = await PreflightAsync(path, origin, method);

            Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, $"{method} {path}: {(int)response.StatusCode}");
            Assert.Equal(origin, Header(response, "Access-Control-Allow-Origin"));
            Assert.Contains("authorization", Header(response, "Access-Control-Allow-Headers")!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("content-type", Header(response, "Access-Control-Allow-Headers")!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(method, Header(response, "Access-Control-Allow-Methods")!);
            // No credentials, no cookies.
            Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
        }
    }

    [Fact]
    public async Task The_allowed_methods_are_exactly_get_post_patch_delete_and_options()
    {
        var response = await PreflightAsync("/projects", "https://www.footlook.co.za");

        var methods = Header(response, "Access-Control-Allow-Methods")!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "DELETE", "GET", "OPTIONS", "PATCH", "POST" }, methods.OrderBy(m => m).ToArray());
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://www.footlook.co.za.evil.example")]
    [InlineData("https://evil.example/https://www.footlook.co.za")]
    [InlineData("http://www.footlook.co.za")]
    [InlineData("https://footlook.co.za:8443")]
    [InlineData("http://localhost:4300")]
    [InlineData("https://localhost:4200")]
    [InlineData("null")]
    public async Task Preflight_from_any_other_origin_gets_no_cors_headers(string origin)
    {
        foreach (var path in new[] { "/auth/microsoft", "/projects", "/projects/prj_aaaaaaaaaaaaaaaa/connect", "/invites/redeem" })
        {
            var response = await PreflightAsync(path, origin);

            Assert.Null(Header(response, "Access-Control-Allow-Origin"));
            Assert.Null(Header(response, "Access-Control-Allow-Methods"));
            Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
        }
    }

    [Fact]
    public async Task A_preflight_asking_for_a_header_that_is_not_allowed_is_not_granted_that_header()
    {
        var response = await PreflightAsync("/projects", "https://www.footlook.co.za", headers: "authorization,x-custom");

        // The origin itself is fine; the unlisted header is simply not in the allowed list, so the browser refuses the request.
        var allowed = Header(response, "Access-Control-Allow-Headers") ?? string.Empty;
        Assert.DoesNotContain("x-custom", allowed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authorization", allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_actual_response_to_an_allowed_origin_carries_the_allow_origin_header_and_to_others_it_does_not()
    {
        var token = await _factory.SessionAsync(_factory.NewUser());

        var allowed = await _factory.SendAsync(HttpMethod.Get, "/projects", token, origin: "http://localhost:4210");
        var evil = await _factory.SendAsync(HttpMethod.Get, "/projects", token, origin: "https://evil.example");

        Assert.Equal("http://localhost:4210", Header(allowed, "Access-Control-Allow-Origin"));
        Assert.Null(Header(allowed, "Access-Control-Allow-Credentials"));
        Assert.Null(Header(evil, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Errors_also_carry_the_cors_header_so_the_site_can_read_them()
    {
        var unauthorized = await _factory.SendAsync(HttpMethod.Get, "/projects", origin: "https://www.footlook.co.za");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("https://www.footlook.co.za", Header(unauthorized, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task The_allowed_origins_can_be_replaced_by_configuration()
    {
        using var custom = new CentralFactory(f => f.Settings["Central:AllowedOrigins:0"] = "https://only.example");
        var request = new HttpRequestMessage(HttpMethod.Options, "/projects");
        request.Headers.Add("Origin", "https://only.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        var ok = await custom.CreateClient().SendAsync(request);

        var request2 = new HttpRequestMessage(HttpMethod.Options, "/projects");
        request2.Headers.Add("Origin", "https://www.footlook.co.za");
        request2.Headers.Add("Access-Control-Request-Method", "GET");
        var no = await custom.CreateClient().SendAsync(request2);

        Assert.Equal("https://only.example", Header(ok, "Access-Control-Allow-Origin"));
        Assert.Null(Header(no, "Access-Control-Allow-Origin"));
    }

    // ---- sign-in rate limit ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Sign_in_is_rate_limited()
    {
        using var limited = new CentralFactory(f => f.Settings["Central:RateLimits:SignInPerMinute"] = "3");

        for (var i = 0; i < 3; i++)
        {
            await CentralFactory.AssertErrorAsync(await limited.PostAsync("/auth/microsoft", body: new { idToken = "garbage", mode = "login" }), HttpStatusCode.Unauthorized, "invalid_token");
        }

        var over = await limited.PostAsync("/auth/microsoft", body: new { idToken = "garbage", mode = "login" });
        await CentralFactory.AssertErrorAsync(over, HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.True(over.Headers.Contains("Retry-After"));
        // Other routes are not affected by the sign-in limit.
        Assert.Equal(HttpStatusCode.OK, (await limited.GetAsync("/.well-known/jwks.json")).StatusCode);
    }
}
