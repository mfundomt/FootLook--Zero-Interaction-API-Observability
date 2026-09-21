using System.Net;
using System.Text.Json;
using FootLook.Central.Data;
using FootLook.Central.Security;
using FootLook.Core.Models;

namespace FootLook.Central.Tests;

public class InviteEndpointTests : IDisposable
{
    private readonly CentralFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> RedeemAsync(FootLookUser user, string code) =>
        await _factory.PostAsync("/invites/redeem", await _factory.SessionAsync(user), new { code });

    private async Task<HttpResponseMessage> RedeemRawAsync(FootLookUser user, string rawBody) =>
        await _factory.SendAsync(HttpMethod.Post, "/invites/redeem", await _factory.SessionAsync(user), rawBody: rawBody);

    // ---- creating ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_an_invite_returns_the_code_once_with_the_contract_shape_and_defaults()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var before = _factory.Time.GetUtcNow().UtcDateTime;

        var response = await _factory.PostAsync($"/projects/{id}/invites", await _factory.SessionAsync(owner));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await CentralFactory.JsonAsync(response);
        var root = doc.RootElement;
        Assert.Equal(new[] { "id", "code", "expiresAtUtc", "maxUses" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Matches("^FL-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{10}$", root.GetProperty("code").GetString()!);
        Assert.Equal(1, root.GetProperty("maxUses").GetInt32());
        var expires = root.GetProperty("expiresAtUtc").GetDateTime();
        Assert.InRange((expires - before).TotalHours, 167.9, 168.1);
        Assert.EndsWith("Z", root.GetProperty("expiresAtUtc").GetString());
    }

    [Fact]
    public async Task Requested_uses_and_lifetime_are_honoured()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);

        var response = await _factory.PostAsync($"/projects/{id}/invites", await _factory.SessionAsync(owner), new { maxUses = 25, expiresInHours = 720 });

        using var doc = await CentralFactory.JsonAsync(response);
        Assert.Equal(25, doc.RootElement.GetProperty("maxUses").GetInt32());
        Assert.InRange((doc.RootElement.GetProperty("expiresAtUtc").GetDateTime() - _factory.Time.GetUtcNow().UtcDateTime).TotalHours, 719.9, 720.1);
    }

    [Theory]
    [InlineData("{\"maxUses\":0}")]
    [InlineData("{\"maxUses\":26}")]
    [InlineData("{\"maxUses\":-1}")]
    [InlineData("{\"maxUses\":1.5}")]
    [InlineData("{\"maxUses\":\"3\"}")]
    [InlineData("{\"expiresInHours\":0}")]
    [InlineData("{\"expiresInHours\":721}")]
    [InlineData("{\"expiresInHours\":-5}")]
    [InlineData("{not json")]
    [InlineData("[1]")]
    public async Task Invalid_invite_settings_are_400(string body)
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);

        var response = await _factory.SendAsync(HttpMethod.Post, $"/projects/{id}/invites", await _factory.SessionAsync(owner), rawBody: body);

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    public async Task An_empty_body_or_empty_object_means_the_defaults(string body)
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);

        var response = await _factory.SendAsync(HttpMethod.Post, $"/projects/{id}/invites", await _factory.SessionAsync(owner), rawBody: body.Length == 0 ? null : body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Only_the_hash_of_a_code_is_ever_stored_and_the_code_is_in_no_other_response()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var token = await _factory.SessionAsync(owner);
        var (inviteId, code) = await _factory.CreateInviteAsync(owner, id);

        // What the store holds is the hash, and nothing in it equals or contains the code.
        var stored = Assert.Single(_factory.Store.AllStoredInviteHashes());
        Assert.Equal(InviteCodes.Hash(code), stored);
        Assert.DoesNotContain(code[3..], stored, StringComparison.OrdinalIgnoreCase);

        // Listing never shows the code or its hash.
        var list = await _factory.GetAsync($"/projects/{id}/invites", token);
        var raw = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(code, raw);
        Assert.DoesNotContain(code[3..], raw);
        Assert.DoesNotContain(stored, raw);
        Assert.DoesNotContain("code", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", raw, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(raw);
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(new[] { "id", "expiresAtUtc", "maxUses", "usedCount", "revoked" }, row.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(inviteId, row.GetProperty("id").GetInt64());
        Assert.Equal(0, row.GetProperty("usedCount").GetInt32());
        Assert.False(row.GetProperty("revoked").GetBoolean());
    }

    [Fact]
    public async Task No_secret_reaches_the_logs_across_the_whole_invite_lifecycle()
    {
        var owner = _factory.NewUser();
        var member = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner, "P", "https://good.example/app");
        var ownerToken = await _factory.SessionAsync(owner);
        var memberToken = await _factory.SessionAsync(member);
        var (_, code) = await _factory.CreateInviteAsync(owner, id);
        await _factory.PostAsync("/invites/redeem", memberToken, new { code });
        await _factory.PostAsync("/invites/redeem", memberToken, new { code = "FL-WRONGWRONG" });
        var connect = await _factory.PostAsync($"/projects/{id}/connect", memberToken, new { returnUrl = "https://good.example/app" });
        using var connectDoc = await CentralFactory.JsonAsync(connect);
        var pass = connectDoc.RootElement.GetProperty("pass").GetString()!;
        var idToken = _factory.MsTokens.Create();
        await _factory.PostAsync("/auth/microsoft", body: new { idToken, mode = "register", acceptedTerms = true });
        await _factory.PostAsync("/auth/microsoft", body: new { idToken = "garbage.token.here", mode = "login" });
        await _factory.GetAsync("/me", "not.a.token");

        var logs = string.Join("\n", _factory.Logs.Lines);
        foreach (var secret in new[] { code, code[3..], InviteCodes.Hash(code), ownerToken, memberToken, pass, idToken, "garbage.token.here" })
        {
            Assert.DoesNotContain(secret, logs);
        }
    }

    [Fact]
    public async Task A_project_can_have_twenty_live_invites_and_the_21st_is_409_until_one_is_revoked_or_used_up()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var token = await _factory.SessionAsync(owner);
        var created = new List<(long Id, string Code)>();
        for (var i = 0; i < 20; i++)
        {
            created.Add(await _factory.CreateInviteAsync(owner, id));
        }

        await CentralFactory.AssertErrorAsync(await _factory.PostAsync($"/projects/{id}/invites", token, new { }), HttpStatusCode.Conflict, "invite_limit");

        // Revoking frees a slot.
        Assert.Equal(HttpStatusCode.NoContent, (await _factory.DeleteAsync($"/projects/{id}/invites/{created[0].Id}", token)).StatusCode);
        created.Add(await _factory.CreateInviteAsync(owner, id));
        await CentralFactory.AssertErrorAsync(await _factory.PostAsync($"/projects/{id}/invites", token, new { }), HttpStatusCode.Conflict, "invite_limit");

        // A used-up invite is no longer live either.
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(_factory.NewUser(), created[1].Code)).StatusCode);
        created.Add(await _factory.CreateInviteAsync(owner, id));
    }

    [Fact]
    public async Task Expired_invites_stop_counting_as_live()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        for (var i = 0; i < 20; i++)
        {
            await _factory.CreateInviteAsync(owner, id, expiresInHours: 1);
        }

        _factory.Time.Advance(TimeSpan.FromHours(2));

        await _factory.CreateInviteAsync(owner, id);
    }

    [Fact]
    public async Task Invites_are_owner_only_a_member_gets_403_and_a_stranger_404()
    {
        var owner = _factory.NewUser();
        var member = _factory.NewUser();
        var stranger = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var (inviteId, code) = await _factory.CreateInviteAsync(owner, id, maxUses: 3);
        await RedeemAsync(member, code);
        var memberToken = await _factory.SessionAsync(member);
        var strangerToken = await _factory.SessionAsync(stranger);

        await CentralFactory.AssertErrorAsync(await _factory.PostAsync($"/projects/{id}/invites", memberToken, new { }), HttpStatusCode.Forbidden, "forbidden");
        await CentralFactory.AssertErrorAsync(await _factory.GetAsync($"/projects/{id}/invites", memberToken), HttpStatusCode.Forbidden, "forbidden");
        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}/invites/{inviteId}", memberToken), HttpStatusCode.Forbidden, "forbidden");

        await CentralFactory.AssertErrorAsync(await _factory.PostAsync($"/projects/{id}/invites", strangerToken, new { }), HttpStatusCode.NotFound, "project_not_found");
        await CentralFactory.AssertErrorAsync(await _factory.GetAsync($"/projects/{id}/invites", strangerToken), HttpStatusCode.NotFound, "project_not_found");
        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}/invites/{inviteId}", strangerToken), HttpStatusCode.NotFound, "project_not_found");

        // Nothing changed.
        var invite = Assert.Single(await _factory.Store.ListInvitesAsync(id));
        Assert.False(invite.Revoked);
    }

    [Fact]
    public async Task An_owner_cannot_revoke_another_projects_invite()
    {
        var a = _factory.NewUser();
        var b = _factory.NewUser();
        var projectA = await _factory.CreateProjectAsync(a);
        var projectB = await _factory.CreateProjectAsync(b);
        var (inviteOfB, code) = await _factory.CreateInviteAsync(b, projectB);

        var response = await _factory.DeleteAsync($"/projects/{projectA}/invites/{inviteOfB}", await _factory.SessionAsync(a));

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.NotFound, "invite_not_found");
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(_factory.NewUser(), code)).StatusCode);
    }

    [Fact]
    public async Task Listing_shows_usage_and_revocation()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var token = await _factory.SessionAsync(owner);
        var (usedId, usedCode) = await _factory.CreateInviteAsync(owner, id, maxUses: 3);
        var (revokedId, _) = await _factory.CreateInviteAsync(owner, id);
        await RedeemAsync(_factory.NewUser(), usedCode);
        await _factory.DeleteAsync($"/projects/{id}/invites/{revokedId}", token);

        using var doc = await CentralFactory.JsonAsync(await _factory.GetAsync($"/projects/{id}/invites", token));
        var rows = doc.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("id").GetInt64());

        Assert.Equal(1, rows[usedId].GetProperty("usedCount").GetInt32());
        Assert.Equal(3, rows[usedId].GetProperty("maxUses").GetInt32());
        Assert.False(rows[usedId].GetProperty("revoked").GetBoolean());
        Assert.True(rows[revokedId].GetProperty("revoked").GetBoolean());
    }

    // ---- revoking -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Revoking_204_is_idempotent_and_the_code_stops_working()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var token = await _factory.SessionAsync(owner);
        var (inviteId, code) = await _factory.CreateInviteAsync(owner, id, maxUses: 5);

        Assert.Equal(HttpStatusCode.NoContent, (await _factory.DeleteAsync($"/projects/{id}/invites/{inviteId}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _factory.DeleteAsync($"/projects/{id}/invites/{inviteId}", token)).StatusCode);

        await CentralFactory.AssertErrorAsync(await RedeemAsync(_factory.NewUser(), code), HttpStatusCode.NotFound, "invalid_invite");
    }

    [Theory]
    [InlineData("999999")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1e3")]
    public async Task Revoking_an_invite_that_does_not_exist_is_404_invite_not_found(string inviteId)
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);

        await CentralFactory.AssertErrorAsync(await _factory.DeleteAsync($"/projects/{id}/invites/{inviteId}", await _factory.SessionAsync(owner)), HttpStatusCode.NotFound, "invite_not_found");
    }

    // ---- redeeming ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Redeeming_makes_the_caller_a_member_and_returns_the_project()
    {
        var owner = _factory.NewUser();
        var tester = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner, "Tested API");
        var (_, code) = await _factory.CreateInviteAsync(owner, id);

        var response = await RedeemAsync(tester, code);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await CentralFactory.JsonAsync(response);
        Assert.Equal(new[] { "projectId", "name", "role" }, doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(id, doc.RootElement.GetProperty("projectId").GetString());
        Assert.Equal("Tested API", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("member", doc.RootElement.GetProperty("role").GetString());

        var detail = await _factory.GetAsync($"/projects/{id}", await _factory.SessionAsync(tester));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailDoc = await CentralFactory.JsonAsync(detail);
        Assert.Equal("member", detailDoc.RootElement.GetProperty("role").GetString());
        Assert.Equal(2, detailDoc.RootElement.GetProperty("memberCount").GetInt32());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task What_a_person_types_is_forgiven(bool lower, bool noHyphen)
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var (_, code) = await _factory.CreateInviteAsync(owner, id);
        var typed = "  " + (noHyphen ? code.Replace("-", "") : code) + " ";
        typed = lower ? typed.ToLowerInvariant() : typed;

        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(_factory.NewUser(), typed)).StatusCode);
    }

    [Fact]
    public async Task A_single_use_code_admits_one_person_and_then_looks_like_any_unknown_code()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var (_, code) = await _factory.CreateInviteAsync(owner, id);
        var late = _factory.NewUser();

        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(_factory.NewUser(), code)).StatusCode);

        await CentralFactory.AssertErrorAsync(await RedeemAsync(late, code), HttpStatusCode.NotFound, "invalid_invite");
        Assert.Equal(2, (await _factory.Store.ListMembersAsync(id)).Count);
    }

    [Fact]
    public async Task A_code_with_several_uses_admits_exactly_that_many_people()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var (_, code) = await _factory.CreateInviteAsync(owner, id, maxUses: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(_factory.NewUser(), code)).StatusCode);
        }

        await CentralFactory.AssertErrorAsync(await RedeemAsync(_factory.NewUser(), code), HttpStatusCode.NotFound, "invalid_invite");
        Assert.Equal(4, (await _factory.Store.ListMembersAsync(id)).Count);
    }

    [Fact]
    public async Task Someone_who_is_already_a_member_gets_200_unchanged_and_no_use_is_consumed()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var (_, code) = await _factory.CreateInviteAsync(owner, id, maxUses: 2);
        var first = _factory.NewUser();

        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(first, code)).StatusCode);
        var again = await RedeemAsync(first, code);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using (var doc = await CentralFactory.JsonAsync(again))
        {
            Assert.Equal("member", doc.RootElement.GetProperty("role").GetString());
        }

        // The repeat did not use the second slot: another person still fits, and only then is it used up.
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(_factory.NewUser(), code)).StatusCode);
        await CentralFactory.AssertErrorAsync(await RedeemAsync(_factory.NewUser(), code), HttpStatusCode.NotFound, "invalid_invite");
        Assert.Equal(3, (await _factory.Store.ListMembersAsync(id)).Count);
    }

    [Fact]
    public async Task The_owner_redeeming_their_own_code_stays_the_owner()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var (_, code) = await _factory.CreateInviteAsync(owner, id);

        var response = await RedeemAsync(owner, code);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await CentralFactory.JsonAsync(response);
        Assert.Equal("owner", doc.RootElement.GetProperty("role").GetString());
        Assert.Single(await _factory.Store.ListMembersAsync(id));
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var (_, code) = await _factory.CreateInviteAsync(owner, id, expiresInHours: 2);

        _factory.Time.Advance(TimeSpan.FromHours(1));
        var stillGood = _factory.NewUser();
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(stillGood, code)).StatusCode);

        _factory.Time.Advance(TimeSpan.FromHours(1.5));
        await CentralFactory.AssertErrorAsync(await RedeemAsync(_factory.NewUser(), code), HttpStatusCode.NotFound, "invalid_invite");
    }

    [Fact]
    public async Task Unknown_malformed_expired_revoked_and_used_up_codes_get_byte_identical_answers()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var token = await _factory.SessionAsync(owner);
        var (_, usedUp) = await _factory.CreateInviteAsync(owner, id);
        await RedeemAsync(_factory.NewUser(), usedUp);
        var (revokedId, revoked) = await _factory.CreateInviteAsync(owner, id);
        await _factory.DeleteAsync($"/projects/{id}/invites/{revokedId}", token);
        var (_, expired) = await _factory.CreateInviteAsync(owner, id, expiresInHours: 1);
        _factory.Time.Advance(TimeSpan.FromHours(2));

        var answers = new List<(HttpStatusCode, string)>();
        foreach (var code in new[] { InviteCodes.Generate(), "FL-NOTACODE", "garbage", usedUp, revoked, expired })
        {
            // Each attempt is from a fresh user, so the throttle never gets in the way.
            var response = await RedeemAsync(_factory.NewUser(), code);
            answers.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        Assert.All(answers, a => Assert.Equal((HttpStatusCode.NotFound, "{\"error\":\"invalid_invite\"}"), a));
    }

    [Fact]
    public async Task A_full_project_is_409_project_full_and_the_code_is_not_consumed()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var (_, code) = await _factory.CreateInviteAsync(owner, id, maxUses: 2);
        for (var i = 0; i < 24; i++)
        {
            _factory.Store.AddMember(id, CentralFactory.IdOf(_factory.NewUser()));
        }

        var latecomer = _factory.NewUser();
        await CentralFactory.AssertErrorAsync(await RedeemAsync(latecomer, code), HttpStatusCode.Conflict, "project_full");

        Assert.Equal(25, (await _factory.Store.ListMembersAsync(id)).Count);
        var invite = Assert.Single(await _factory.Store.ListInvitesAsync(id));
        Assert.Equal(0, invite.UsedCount);

        // Room appears when a member leaves: the same code works.
        var someone = (await _factory.Store.ListMembersAsync(id)).First(m => m.Role == "member");
        await _factory.DeleteAsync($"/projects/{id}/members/{someone.UserId}", await _factory.SessionAsync(owner));
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(latecomer, code)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("{\"code\":null}")]
    [InlineData("{\"code\":\"\"}")]
    [InlineData("{\"code\":\"   \"}")]
    [InlineData("{\"code\":5}")]
    [InlineData("{not json")]
    [InlineData("[]")]
    public async Task A_request_with_no_usable_code_is_400_invalid_request(string body)
    {
        var response = await RedeemRawAsync(_factory.NewUser(), body);

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task An_oversized_body_is_400()
    {
        var response = await RedeemRawAsync(_factory.NewUser(), "{\"code\":\"" + new string('A', 20_000) + "\"}");

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_code_for_the_wrong_kind_of_thing_such_as_a_sql_string_is_just_invalid()
    {
        var response = await RedeemAsync(_factory.NewUser(), "FL-'; DROP TABLE Projects;--");

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.NotFound, "invalid_invite");
    }

    // ---- throttle --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ten_failed_redemptions_in_fifteen_minutes_lock_that_user_out_even_for_a_good_code()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var (_, code) = await _factory.CreateInviteAsync(owner, id);
        var guesser = _factory.NewUser();

        for (var i = 0; i < 10; i++)
        {
            await CentralFactory.AssertErrorAsync(await RedeemAsync(guesser, InviteCodes.Generate()), HttpStatusCode.NotFound, "invalid_invite");
        }

        var blocked = await RedeemAsync(guesser, code);
        await CentralFactory.AssertErrorAsync(blocked, HttpStatusCode.TooManyRequests, "too_many_attempts");
        Assert.Single(await _factory.Store.ListMembersAsync(id));

        // Others are unaffected.
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(_factory.NewUser(), code)).StatusCode);

        // The lock lifts once the failures are older than the window.
        _factory.Time.Advance(TimeSpan.FromMinutes(16));
        var (_, another) = await _factory.CreateInviteAsync(owner, id);
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(guesser, another)).StatusCode);
    }

    [Fact]
    public async Task Malformed_codes_count_as_failures_too_and_successes_do_not_count()
    {
        var owner = _factory.NewUser();
        var id = await _factory.CreateProjectAsync(owner);
        var user = _factory.NewUser();

        for (var i = 0; i < 9; i++)
        {
            await RedeemAsync(user, "nonsense");
        }

        var (_, code) = await _factory.CreateInviteAsync(owner, id);
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(user, code)).StatusCode);
        // Nine failures plus a success is still under the limit: one more failure is the tenth.
        await CentralFactory.AssertErrorAsync(await RedeemAsync(user, "nonsense"), HttpStatusCode.NotFound, "invalid_invite");
        await CentralFactory.AssertErrorAsync(await RedeemAsync(user, "nonsense"), HttpStatusCode.TooManyRequests, "too_many_attempts");
    }

    [Fact]
    public async Task Redeem_is_rate_limited_per_user()
    {
        using var limited = new CentralFactory(f => f.Settings["Central:RateLimits:RedeemPerMinute"] = "3");
        var busy = limited.NewUser();
        var calm = limited.NewUser();
        var busyToken = await limited.SessionAsync(busy);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await limited.PostAsync("/invites/redeem", busyToken, new { code = "FL-AAAAAAAAAA" })).StatusCode);
        }

        var over = await limited.PostAsync("/invites/redeem", busyToken, new { code = "FL-AAAAAAAAAA" });
        await CentralFactory.AssertErrorAsync(over, HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.True(over.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.NotFound, (await limited.PostAsync("/invites/redeem", await limited.SessionAsync(calm), new { code = "FL-AAAAAAAAAA" })).StatusCode);
    }
}
