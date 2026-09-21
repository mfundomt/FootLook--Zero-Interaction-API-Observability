using System.Net;
using System.Text.Json;
using FootLook.Core.Models;

namespace FootLook.Central.Tests;

public class ProjectEndpointTests : IDisposable
{
    private const string MissingId = "prj_zzzzzzzzzzzzzzzz";

    private readonly CentralFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> PostProjectAsync(FootLookUser user, object body) =>
        await _factory.PostAsync("/projects", await _factory.SessionAsync(user), body);

    // ---- every route needs a central session token -----------------------------------------------------------

    [Theory]
    [InlineData("GET", "/me")]
    [InlineData("GET", "/projects")]
    [InlineData("POST", "/projects")]
    [InlineData("GET", "/projects/prj_aaaaaaaaaaaaaaaa")]
    [InlineData("PATCH", "/projects/prj_aaaaaaaaaaaaaaaa")]
    [InlineData("DELETE", "/projects/prj_aaaaaaaaaaaaaaaa")]
    [InlineData("GET", "/projects/prj_aaaaaaaaaaaaaaaa/members")]
    [InlineData("DELETE", "/projects/prj_aaaaaaaaaaaaaaaa/members/11111111111111111111111111111111")]
    [InlineData("POST", "/projects/prj_aaaaaaaaaaaaaaaa/invites")]
    [InlineData("GET", "/projects/prj_aaaaaaaaaaaaaaaa/invites")]
    [InlineData("DELETE", "/projects/prj_aaaaaaaaaaaaaaaa/invites/1")]
    [InlineData("POST", "/projects/prj_aaaaaaaaaaaaaaaa/connect")]
    [InlineData("POST", "/invites/redeem")]
    public async Task Without_a_token_every_route_is_401(string method, string path)
    {
        var response = await _factory.SendAsync(new HttpMethod(method), path, body: method == "GET" || method == "DELETE" ? null : new { });

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    // ---- create ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_project_returns_201_with_the_contract_shape_and_makes_the_caller_owner()
    {
        var owner = _factory.NewUser();

        var response = await PostProjectAsync(owner, new { name = "  Billing API  ", allowedReturnUrls = new[] { "HTTPS://Billing.Example.com/footlook.html" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await CentralFactory.JsonAsync(response);
        var root = doc.RootElement;
        Assert.Equal(new[] { "id", "name", "role", "allowedReturnUrls", "createdAtUtc" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Matches("^prj_[a-z2-7]{16}$", root.GetProperty("id").GetString()!);
        Assert.Equal("Billing API", root.GetProperty("name").GetString());
        Assert.Equal("owner", root.GetProperty("role").GetString());
        Assert.Equal(new[] { "https://billing.example.com/footlook.html" }, root.GetProperty("allowedReturnUrls").EnumerateArray().Select(u => u.GetString()).ToArray());
        Assert.EndsWith("Z", root.GetProperty("createdAtUtc").GetString());

        var members = await _factory.Store.ListMembersAsync(root.GetProperty("id").GetString()!);
        var member = Assert.Single(members);
        Assert.Equal("owner", member.Role);
        Assert.Equal(owner.Id, member.UserId);
    }

    [Fact]
    public async Task Project_ids_are_unique_and_unguessable_looking()
    {
        var owner = _factory.NewUser();
        var ids = new HashSet<string>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add(await _factory.CreateProjectAsync(owner));
        }

        Assert.Equal(10, ids.Count);
    }

    [Fact]
    public async Task A_project_without_return_urls_is_fine()
    {
        var owner = _factory.NewUser();

        using var doc = await CentralFactory.JsonAsync(await PostProjectAsync(owner, new { name = "No urls" }));

        Assert.Equal(0, doc.RootElement.GetProperty("allowedReturnUrls").GetArrayLength());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"\"}")]
    [InlineData("{\"name\":\"   \"}")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":5}")]
    [InlineData("{\"name\":\"a\\u0007b\"}")]
    [InlineData("{\"name\":\"ok\",\"allowedReturnUrls\":\"https://a.example/x\"}")]
    [InlineData("{\"name\":\"ok\",\"allowedReturnUrls\":[null]}")]
    [InlineData("{\"name\":\"ok\",\"allowedReturnUrls\":[5]}")]
    [InlineData("{\"name\":\"ok\",\"allowedReturnUrls\":[\"http://evil.example/x\"]}")]
    [InlineData("{\"name\":\"ok\",\"allowedReturnUrls\":[\"https://user@a.example/x\"]}")]
    [InlineData("{\"name\":\"ok\",\"allowedReturnUrls\":[\"https://a.example/x#frag\"]}")]
    [InlineData("{\"name\":\"ok\",\"allowedReturnUrls\":[\"https://*.example/x\"]}")]
    [InlineData("{\"name\":\"ok\",\"allowedReturnUrls\":[\"javascript:alert(1)\"]}")]
    [InlineData("{\"name\":\"ok\",\"allowedReturnUrls\":[\"/relative\"]}")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Invalid_create_requests_are_400_invalid_request(string body)
    {
        var owner = _factory.NewUser();

        var response = await _factory.SendAsync(HttpMethod.Post, "/projects", await _factory.SessionAsync(owner), rawBody: body);

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, _factory.Store.ProjectCount);
    }

    [Fact]
    public async Task Names_of_one_and_of_100_characters_are_accepted_and_101_is_not()
    {
        var owner = _factory.NewUser();

        Assert.Equal(HttpStatusCode.Created, (await PostProjectAsync(owner, new { name = "a" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostProjectAsync(owner, new { name = new string('b', 100) })).StatusCode);
        await CentralFactory.AssertErrorAsync(await PostProjectAsync(owner, new { name = new string('c', 101) }), HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task More_than_five_return_urls_are_refused_and_five_are_fine()
    {
        var owner = _factory.NewUser();
        string[] Urls(int n) => Enumerable.Range(0, n).Select(i => $"https://app{i}.example.com/x").ToArray();

        Assert.Equal(HttpStatusCode.Created, (await PostProjectAsync(owner, new { name = "five", allowedReturnUrls = Urls(5) })).StatusCode);
        await CentralFactory.AssertErrorAsync(await PostProjectAsync(owner, new { name = "six", allowedReturnUrls = Urls(6) }), HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Duplicate_return_urls_are_stored_once()
    {
        var owner = _factory.NewUser();

        using var doc = await CentralFactory.JsonAsync(await PostProjectAsync(owner, new { name = "dup", allowedReturnUrls = new[] { "https://a.example/x", "HTTPS://A.EXAMPLE/x" } }));

        Assert.Single(doc.RootElement.GetProperty("allowedReturnUrls").EnumerateArray());
    }

    [Fact]
    public async Task A_user_may_own_ten_projects_and_the_eleventh_is_409_project_limit()
    {
        var owner = _factory.NewUser();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await PostProjectAsync(owner, new { name = $"p{i}" })).StatusCode);
        }

        await CentralFactory.AssertErrorAsync(await PostProjectAsync(owner, new { name = "one too many" }), HttpStatusCode.Conflict, "project_limit");
        // The limit is per owner.
        Assert.Equal(HttpStatusCode.Created, (await PostProjectAsync(_factory.NewUser(), new { name = "someone else" })).StatusCode);
    }

    [Fact]
    public async Task Projects_a_user_merely_joined_do_not_count_toward_their_limit_and_deleting_frees_a_slot()
    {
        var owner = _factory.NewUser();
        var other = _factory.NewUser();
        for (var i = 0; i < 10; i++)
        {
            await _factory.CreateProjectAsync(owner, $"p{i}");
        }

        var joinable = await _factory.CreateProjectAsync(other);
        var (_, code) = await _factory.CreateInviteAsync(other, joinable);
        Assert.Equal(HttpStatusCode.OK, (await _factory.PostAsync("/invites/redeem", await _factory.SessionAsync(owner), new { code })).StatusCode);

        // The owner is a member of 11 projects but still owns 10, so the limit message stays.
        await CentralFactory.AssertErrorAsync(await PostProjectAsync(owner, new { name = "x" }), HttpStatusCode.Conflict, "project_limit");

        var projects = await _factory.Store.ListProjectsAsync(CentralFactory.IdOf(owner));
        var toDelete = projects.First(p => p.Role == "owner").Id;
        Assert.Equal(HttpStatusCode.NoContent, (await _factory.DeleteAsync($"/projects/{toDelete}", await _factory.SessionAsync(owner))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostProjectAsync(owner, new { name = "fits now" })).StatusCode);
    }

    // ---- list / get --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Listing_shows_owned_and_joined_projects_with_member_counts_and_nobody_elses()
    {
        var owner = _factory.NewUser();
        var friend = _factory.NewUser();
        var stranger = _factory.NewUser();
        var mine = await _factory.CreateProjectAsync(owner, "Mine");
        var theirs = await _factory.CreateProjectAsync(friend, "Theirs");
        await _factory.CreateProjectAsync(stranger, "Secret");
        var (_, code) = await _factory.CreateInviteAsync(friend, theirs);
        await _factory.PostAsync("/invites/redeem", await _factory.SessionAsync(owner), new { code });

        var response = await _factory.GetAsync("/projects", await _factory.SessionAsync(owner));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await CentralFactory.JsonAsync(response);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(new[] { "id", "name", "role", "memberCount", "createdAtUtc" }, i.EnumerateObject().Select(p => p.Name).ToArray()));
        var mineItem = items.Single(i => i.GetProperty("id").GetString() == mine);
        var theirsItem = items.Single(i => i.GetProperty("id").GetString() == theirs);
        Assert.Equal("owner", mineItem.GetProperty("role").GetString());
        Assert.Equal(1, mineItem.GetProperty("memberCount").GetInt32());
        Assert.Equal("member", theirsItem.GetProperty("role").GetString());
        Assert.Equal(2, theirsItem.GetProperty("memberCount").GetInt32());
    }

    [Fact]
    public async Task A_user_with_no_projects_gets_an_empty_list()
    {
        var response = await _factory.GetAsync("/projects", await _factory.SessionAsync(_factory.NewUser()));

        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Any_member_can_read_the_project_details_with_their_own_role()
    {
        var owner = _factory.NewUser();
        var member = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner, "Details", "https://a.example/x");
        var (_, code) = await _factory.CreateInviteAsync(owner, id);
        await _factory.PostAsync("/invites/redeem", await _factory.SessionAsync(member), new { code });

        foreach (var (user, role) in new[] { (owner, "owner"), (member, "member") })
        {
            var response = await _factory.GetAsync($"/projects/{id}", await _factory.SessionAsync(user));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = await CentralFactory.JsonAsync(response);
            var root = doc.RootElement;
            Assert.Equal(new[] { "id", "name", "role", "allowedReturnUrls", "createdAtUtc", "memberCount" }, root.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(id, root.GetProperty("id").GetString());
            Assert.Equal("Details", root.GetProperty("name").GetString());
            Assert.Equal(role, root.GetProperty("role").GetString());
            Assert.Equal(2, root.GetProperty("memberCount").GetInt32());
            Assert.Equal("https://a.example/x", Assert.Single(root.GetProperty("allowedReturnUrls").EnumerateArray()).GetString());
        }
    }

    [Fact]
    public async Task A_non_member_gets_404_project_not_found_never_403_and_it_is_the_same_as_for_a_project_that_does_not_exist()
    {
        var owner = _factory.NewUser();
        var stranger = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var token = await _factory.SessionAsync(stranger);

        var real = await _factory.GetAsync($"/projects/{id}", token);
        var missing = await _factory.GetAsync($"/projects/{MissingId}", token);

        await CentralFactory.AssertErrorAsync(real, HttpStatusCode.NotFound, "project_not_found");
        await CentralFactory.AssertErrorAsync(missing, HttpStatusCode.NotFound, "project_not_found");
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await real.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("prj_")]
    [InlineData("PRJ_AAAAAAAAAAAAAAAA")]
    [InlineData("prj_aaaaaaaaaaaaaaa1")]
    [InlineData("1' OR '1'='1")]
    [InlineData("..%2f..%2fadmin")]
    public async Task Malformed_project_ids_are_404_project_not_found_on_every_project_route(string id)
    {
        var token = await _factory.SessionAsync(_factory.NewUser());
        var encoded = Uri.EscapeDataString(id);

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, $"/projects/{encoded}", null),
                     (HttpMethod.Patch, $"/projects/{encoded}", new { name = "x" }),
                     (HttpMethod.Delete, $"/projects/{encoded}", null),
                     (HttpMethod.Get, $"/projects/{encoded}/members", null),
                     (HttpMethod.Post, $"/projects/{encoded}/invites", new { }),
                     (HttpMethod.Get, $"/projects/{encoded}/invites", null),
                     (HttpMethod.Post, $"/projects/{encoded}/connect", new { returnUrl = "http://localhost:4200/" }),
                 })
        {
            var response = await _factory.SendAsync(method, path, token, body);
            // Nothing that is not a project id can ever be a member's project: 404 (an odd path may not even route).
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path}: {(int)response.StatusCode}");
        }
    }

    // ---- update / delete: owner only ------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_can_rename_and_replace_the_return_urls()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner, "Old", "https://old.example/x");

        var response = await _factory.PatchAsync($"/projects/{id}", await _factory.SessionAsync(owner), new { name = "New", allowedReturnUrls = new[] { "https://new.example/y", "http://localhost:5103/footlook.html" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await CentralFactory.JsonAsync(response);
        Assert.Equal("New", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("owner", doc.RootElement.GetProperty("role").GetString());
        Assert.Equal(new[] { "http://localhost:5103/footlook.html", "https://new.example/y" }, doc.RootElement.GetProperty("allowedReturnUrls").EnumerateArray().Select(u => u.GetString()).OrderBy(u => u).ToArray());
        Assert.Equal(1, doc.RootElement.GetProperty("memberCount").GetInt32());
    }

    [Fact]
    public async Task Patching_only_the_name_keeps_the_urls_and_patching_only_urls_keeps_the_name_and_an_empty_list_clears_them()
    {
        var owner = _factory.NewUser();
        var token = await _factory.SessionAsync(owner);
        var id = await _factory.CreateProjectAsync(owner, "Keep", "https://keep.example/x");

        using (var a = await CentralFactory.JsonAsync(await _factory.PatchAsync($"/projects/{id}", token, new { name = "Renamed" })))
        {
            Assert.Single(a.RootElement.GetProperty("allowedReturnUrls").EnumerateArray());
        }

        using (var b = await CentralFactory.JsonAsync(await _factory.PatchAsync($"/projects/{id}", token, new { allowedReturnUrls = new[] { "https://other.example/z" } })))
        {
            Assert.Equal("Renamed", b.RootElement.GetProperty("name").GetString());
        }

        using var c = await CentralFactory.JsonAsync(await _factory.PatchAsync($"/projects/{id}", token, new { allowedReturnUrls = Array.Empty<string>() }));
        Assert.Equal(0, c.RootElement.GetProperty("allowedReturnUrls").GetArrayLength());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"\"}")]
    [InlineData("{\"name\":\"   \"}")]
    [InlineData("{\"allowedReturnUrls\":[\"http://evil.example/x\"]}")]
    [InlineData("{\"allowedReturnUrls\":[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\"]}")]
    [InlineData("nope")]
    [InlineData("")]
    public async Task Invalid_patches_are_400_and_change_nothing(string body)
    {
        var owner = _factory.NewUser();
        var token = await _factory.SessionAsync(owner);
        var id = await _factory.CreateProjectAsync(owner, "Stable", "https://stable.example/x");

        var response = await _factory.SendAsync(HttpMethod.Patch, $"/projects/{id}", token, rawBody: body);

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        var detail = await _factory.Store.GetProjectAsync(id, CentralFactory.IdOf(owner));
        Assert.Equal("Stable", detail!.Name);
        Assert.Equal(new[] { "https://stable.example/x" }, detail.AllowedReturnUrls);
    }

    [Fact]
    public async Task A_member_cannot_patch_or_delete_a_project_403_and_a_non_member_gets_404()
    {
        var owner = _factory.NewUser();
        var member = _factory.NewUser();
        var stranger = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner, "Mine");
        var (_, code) = await _factory.CreateInviteAsync(owner, id);
        await _factory.PostAsync("/invites/redeem", await _factory.SessionAsync(member), new { code });
        var memberToken = await _factory.SessionAsync(member);
        var strangerToken = await _factory.SessionAsync(stranger);

        await CentralFactory.AssertErrorAsync(await _factory.PatchAsync($"/projects/{id}", memberToken, new { name = "Hacked" }), HttpStatusCode.Forbidden, "forbidden");
        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}", memberToken), HttpStatusCode.Forbidden, "forbidden");
        await CentralFactory.AssertErrorAsync(await _factory.PatchAsync($"/projects/{id}", strangerToken, new { name = "Hacked" }), HttpStatusCode.NotFound, "project_not_found");
        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}", strangerToken), HttpStatusCode.NotFound, "project_not_found");

        Assert.Equal("Mine", (await _factory.Store.GetProjectAsync(id, CentralFactory.IdOf(owner)))!.Name);
    }

    [Fact]
    public async Task Deleting_removes_the_project_its_members_and_its_invites()
    {
        var owner = _factory.NewUser();
        var member = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var (_, code) = await _factory.CreateInviteAsync(owner, id, maxUses: 5);
        await _factory.PostAsync("/invites/redeem", await _factory.SessionAsync(member), new { code });

        var response = await _factory.DeleteAsync($"/projects/{id}", await _factory.SessionAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
        await CentralFactory.AssertErrorAsync(await _factory.GetAsync($"/projects/{id}", await _factory.SessionAsync(owner)), HttpStatusCode.NotFound, "project_not_found");
        await CentralFactory.AssertErrorAsync(await _factory.GetAsync($"/projects/{id}", await _factory.SessionAsync(member)), HttpStatusCode.NotFound, "project_not_found");
        Assert.Empty(_factory.Store.AllStoredInviteHashes());
        Assert.Empty(await _factory.Store.ListProjectsAsync(CentralFactory.IdOf(member)));
        // The code died with the project.
        await CentralFactory.AssertErrorAsync(await _factory.PostAsync("/invites/redeem", await _factory.SessionAsync(_factory.NewUser()), new { code }), HttpStatusCode.NotFound, "invalid_invite");
    }

    [Fact]
    public async Task Deleting_twice_is_404_the_second_time()
    {
        var owner = _factory.NewUser();
        var token = await _factory.SessionAsync(owner);
        var id = await _factory.CreateProjectAsync(owner);

        Assert.Equal(HttpStatusCode.NoContent, (await _factory.DeleteAsync($"/projects/{id}", token)).StatusCode);
        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}", token), HttpStatusCode.NotFound, "project_not_found");
    }

    // ---- members -------------------------------------------------------------------------------------------------------

    private async Task<(FootLookUser Owner, FootLookUser Member, string ProjectId)> ProjectWithMemberAsync()
    {
        var owner = _factory.NewUser("owner");
        var member = _factory.NewUser("member");
        var id = await _factory.CreateProjectAsync(owner);
        var (_, code) = await _factory.CreateInviteAsync(owner, id);
        Assert.Equal(HttpStatusCode.OK, (await _factory.PostAsync("/invites/redeem", await _factory.SessionAsync(member), new { code })).StatusCode);
        return (owner, member, id);
    }

    [Fact]
    public async Task Any_member_can_list_the_members_owner_first()
    {
        var (owner, member, id) = await ProjectWithMemberAsync();

        foreach (var caller in new[] { owner, member })
        {
            var response = await _factory.GetAsync($"/projects/{id}/members", await _factory.SessionAsync(caller));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = await CentralFactory.JsonAsync(response);
            var rows = doc.RootElement.EnumerateArray().ToList();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(new[] { "userId", "email", "displayName", "role", "addedAtUtc" }, r.EnumerateObject().Select(p => p.Name).ToArray()));
            Assert.Equal(new[] { "owner", "member" }, rows.Select(r => r.GetProperty("role").GetString()).ToArray());
            Assert.Equal(owner.Id, rows[0].GetProperty("userId").GetString());
            Assert.Equal("owner@example.invalid", rows[0].GetProperty("email").GetString());
            Assert.Equal("member", rows[1].GetProperty("displayName").GetString());
            Assert.EndsWith("Z", rows[1].GetProperty("addedAtUtc").GetString());
        }
    }

    [Fact]
    public async Task A_non_member_cannot_list_members()
    {
        var (_, _, id) = await ProjectWithMemberAsync();

        await CentralFactory.AssertErrorAsync(await _factory.GetAsync($"/projects/{id}/members", await _factory.SessionAsync(_factory.NewUser())), HttpStatusCode.NotFound, "project_not_found");
    }

    [Fact]
    public async Task The_owner_can_remove_a_member_who_then_loses_access()
    {
        var (owner, member, id) = await ProjectWithMemberAsync();

        var response = await _factory.DeleteAsync($"/projects/{id}/members/{member.Id}", await _factory.SessionAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await CentralFactory.AssertErrorAsync(await _factory.GetAsync($"/projects/{id}", await _factory.SessionAsync(member)), HttpStatusCode.NotFound, "project_not_found");
        await CentralFactory.AssertErrorAsync(await _factory.PostAsync($"/projects/{id}/connect", await _factory.SessionAsync(member), new { returnUrl = "http://localhost:4200/x" }), HttpStatusCode.NotFound, "project_not_found");
        Assert.Single(await _factory.Store.ListMembersAsync(id));
    }

    [Fact]
    public async Task A_member_can_remove_themself_but_nobody_else()
    {
        var (owner, member, id) = await ProjectWithMemberAsync();
        var third = _factory.NewUser();
        var (_, code) = await _factory.CreateInviteAsync(owner, id);
        await _factory.PostAsync("/invites/redeem", await _factory.SessionAsync(third), new { code });
        var memberToken = await _factory.SessionAsync(member);

        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}/members/{third.Id}", memberToken), HttpStatusCode.Forbidden, "forbidden");
        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}/members/{owner.Id}", memberToken), HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(3, (await _factory.Store.ListMembersAsync(id)).Count);

        Assert.Equal(HttpStatusCode.NoContent, (await _factory.DeleteAsync($"/projects/{id}/members/{member.Id}", memberToken)).StatusCode);
        Assert.Equal(2, (await _factory.Store.ListMembersAsync(id)).Count);
    }

    [Fact]
    public async Task The_owner_cannot_be_removed_by_anyone_including_themself()
    {
        var (owner, member, id) = await ProjectWithMemberAsync();

        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}/members/{owner.Id}", await _factory.SessionAsync(owner)), HttpStatusCode.BadRequest, "cannot_remove_owner");
        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}/members/{owner.Id}", await _factory.SessionAsync(member)), HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(2, (await _factory.Store.ListMembersAsync(id)).Count);
    }

    [Fact]
    public async Task Removing_someone_who_is_not_a_member_is_404_member_not_found_and_a_non_member_caller_is_404_project_not_found()
    {
        var (owner, _, id) = await ProjectWithMemberAsync();
        var stranger = _factory.NewUser();

        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}/members/{stranger.Id}", await _factory.SessionAsync(owner)), HttpStatusCode.NotFound, "member_not_found");
        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}/members/not-a-guid", await _factory.SessionAsync(owner)), HttpStatusCode.NotFound, "member_not_found");
        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}/members/{owner.Id}", await _factory.SessionAsync(stranger)), HttpStatusCode.NotFound, "project_not_found");
    }

    // ---- listing shape of json ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Responses_are_not_cacheable_and_carry_no_cookies()
    {
        var owner = _factory.NewUser();
        var response = await _factory.GetAsync("/projects", await _factory.SessionAsync(owner));

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task An_unexpected_store_failure_is_500_server_error_and_leaks_nothing()
    {
        var owner = _factory.NewUser();
        var token = await _factory.SessionAsync(owner);
        _factory.Store.ThrowOnEveryCall = new InvalidOperationException("Server=secret.example;User Id=sa;Password=hunter2");

        var response = await _factory.GetAsync("/projects", token);

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.InternalServerError, "server_error");
        Assert.DoesNotContain("hunter2", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(_factory.Logs.Lines, l => l.Contains("hunter2") || l.Contains("secret.example"));
    }

    [Fact]
    public async Task Without_a_database_the_project_routes_are_503_service_unavailable()
    {
        using var noDb = new CentralFactory(f => f.UseFakeStores = false);
        var token = await noDb.SessionAsync(noDb.NewUser());

        await CentralFactory.AssertErrorAsync(await noDb.GetAsync("/projects", token), HttpStatusCode.ServiceUnavailable, "service_unavailable");
        await CentralFactory.AssertErrorAsync(await noDb.PostAsync("/projects", token, new { name = "x" }), HttpStatusCode.ServiceUnavailable, "service_unavailable");
    }
}
