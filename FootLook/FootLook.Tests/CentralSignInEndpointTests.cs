using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FootLook.Core.Interfaces;
using FootLook.Core.Options;
using FootLook.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Tests;

public class CentralSignInEndpointTests : IAsyncLifetime
{
    private const string Exchange = "/footlook/auth/exchange";

    private readonly CentralTestKeys _keys = new();
    private readonly StaticKeyProvider _provider;
    private FootLookTestHost _host = null!;

    public CentralSignInEndpointTests()
    {
        _provider = _keys.Provider();
    }

    public async Task InitializeAsync() => _host = await StartAsync();

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _keys.Dispose();
    }

    private Task<FootLookTestHost> StartAsync(Action<FootLookOptions>? configure = null, ICentralKeyProvider? provider = null) =>
        FootLookTestHost.StartAsync(
            o =>
            {
                o.Central.ProjectId = CentralTestKeys.ProjectId;
                o.Central.Issuer = CentralTestKeys.Issuer;
                configure?.Invoke(o);
            },
            services => services.AddSingleton(provider ?? _provider));

    private Task<HttpResponseMessage> ExchangeAsync(Action<PassSpec>? spec = null, FootLookTestHost? host = null) =>
        (host ?? _host).PostAsync(Exchange, body: new { pass = _keys.Create(spec) });

    private Task<HttpResponseMessage> ExchangeRawAsync(string pass, FootLookTestHost? host = null) =>
        (host ?? _host).PostAsync(Exchange, body: new { pass });

    private static async Task<string> TokenOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await FootLookTestHost.ReadJsonAsync(response);
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        // Exactly { "error": "<code>" } - no reasons, no exception text.
        Assert.Equal(new[] { "error" }, doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(error, doc.RootElement.GetProperty("error").GetString());
    }

    // ---- success ------------------------------------------------------------------

    [Fact]
    public async Task Valid_pass_returns_200_with_the_contract_shape()
    {
        var response = await ExchangeAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.False(string.IsNullOrEmpty(root.GetProperty("token").GetString()));
        Assert.True(root.GetProperty("expiresAtUtc").GetDateTime() > DateTime.UtcNow.AddHours(7));
        Assert.EndsWith("Z", root.GetProperty("expiresAtUtc").GetString());
        Assert.False(root.GetProperty("isAdmin").GetBoolean());

        var user = root.GetProperty("user");
        Assert.Equal("central:0f8e6a52-3c1d-4e0b-9a67-0c5d2f1b7a11", user.GetProperty("id").GetString());
        Assert.Equal("dev@example.invalid", user.GetProperty("email").GetString());
        Assert.Equal("Dev Person", user.GetProperty("displayName").GetString());
        Assert.Equal(3, user.EnumerateObject().Count());
        Assert.Equal(4, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task Issued_token_opens_the_session_and_works_on_protected_routes()
    {
        var token = await TokenOf(await ExchangeAsync());

        var me = await _host.GetAsync("/footlook/auth/me", token);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var meDoc = await FootLookTestHost.ReadJsonAsync(me);
        Assert.True(meDoc.RootElement.GetProperty("observationActive").GetBoolean());
        Assert.Equal(token, meDoc.RootElement.GetProperty("token").GetString());
        Assert.False(string.IsNullOrEmpty(meDoc.RootElement.GetProperty("sessionId").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync("/footlook/captures", token)).StatusCode);
    }

    [Fact]
    public async Task Auth_me_reads_a_central_user_back_from_the_token_and_never_touches_the_stores()
    {
        var token = await TokenOf(await ExchangeAsync(s => { s.Role = "owner"; s.Name = "Ada Owner"; s.Email = "ada@example.invalid"; }));

        using var me = await FootLookTestHost.ReadJsonAsync(await _host.GetAsync("/footlook/auth/me", token));
        var user = me.RootElement.GetProperty("user");

        Assert.Equal("central:0f8e6a52-3c1d-4e0b-9a67-0c5d2f1b7a11", user.GetProperty("id").GetString());
        Assert.Equal("ada@example.invalid", user.GetProperty("email").GetString());
        Assert.Equal("Ada Owner", user.GetProperty("displayName").GetString());
        Assert.True(user.GetProperty("isAdmin").GetBoolean());
        Assert.DoesNotContain("passwordHash", me.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // The transient user was never written to the local account store.
        Assert.Null(_host.Services.GetRequiredService<IFootLookUserStore>().FindById("central:0f8e6a52-3c1d-4e0b-9a67-0c5d2f1b7a11"));
    }

    [Fact]
    public async Task Owner_is_admin_and_member_is_not()
    {
        var owner = await TokenOf(await ExchangeAsync(s => { s.Role = "owner"; s.Jti = "j-owner"; }));
        var member = await TokenOf(await ExchangeAsync(s => { s.Role = "member"; s.Subject = "someone-else"; s.Jti = "j-member"; }));

        using var ownerBody = await FootLookTestHost.ReadJsonAsync(await ExchangeAsync(s => { s.Role = "owner"; s.Jti = "j-owner-2"; }));
        Assert.True(ownerBody.RootElement.GetProperty("isAdmin").GetBoolean());

        // Admin-only route: owner passes, member is refused.
        Assert.NotEqual(HttpStatusCode.Forbidden, (await _host.PostAsync("/footlook/dev/self-heal", owner)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.PostAsync("/footlook/dev/self-heal", member)).StatusCode);
    }

    [Fact]
    public async Task Missing_name_falls_back_to_the_local_part_of_the_email()
    {
        using var doc = await FootLookTestHost.ReadJsonAsync(await ExchangeAsync(s => { s.Omit.Add("name"); s.Email = "grace@example.invalid"; }));

        Assert.Equal("grace", doc.RootElement.GetProperty("user").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Captures_are_scoped_to_the_session_and_cleared_on_sign_out()
    {
        var token1 = await TokenOf(await ExchangeAsync());
        await _host.GetAsync("/hello");
        await _host.WaitForCaptureAsync(token1, "/hello");
        Assert.Contains("/hello", await _host.GetCapturePathsAsync(token1));

        // A second person's session (a different account) does not see the first one's captures.
        var other = await TokenOf(await ExchangeAsync(s => s.Subject = "another-account"));
        Assert.DoesNotContain("/hello", await _host.GetCapturePathsAsync(other));

        Assert.Equal(HttpStatusCode.OK, (await _host.PostAsync("/footlook/auth/logout", token1)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/auth/me", token1)).StatusCode);

        // Signing back in with a fresh pass is a fresh, empty session.
        var token2 = await TokenOf(await ExchangeAsync());
        Assert.Empty(await _host.GetCapturePathsAsync(token2));
        // Nothing from the ended session is left in the capture store.
        Assert.Empty(_host.Store.GetAll());
    }

    // ---- rejection ----------------------------------------------------------------

    [Fact]
    public async Task Wrong_audience_is_rejected() =>
        await AssertErrorAsync(await ExchangeAsync(s => s.Audience = "footlook-central"), HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task A_pass_minted_for_another_project_is_rejected() =>
        await AssertErrorAsync(await ExchangeAsync(s => s.Audience = "prj_someoneelses0001"), HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task A_pass_addressed_to_this_and_another_project_is_rejected() =>
        await AssertErrorAsync(await ExchangeAsync(s => s.Extra["aud"] = new[] { CentralTestKeys.ProjectId, "prj_someoneelses0001" }),
            HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task Wrong_issuer_is_rejected() =>
        await AssertErrorAsync(await ExchangeAsync(s => s.Issuer = "https://evil.test"), HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task Issuer_must_match_exactly_including_a_trailing_slash() =>
        await AssertErrorAsync(await ExchangeAsync(s => s.Issuer = CentralTestKeys.Issuer + "/"), HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task Expired_pass_is_rejected() =>
        await AssertErrorAsync(await ExchangeAsync(s =>
        {
            s.IssuedAt = DateTime.UtcNow.AddMinutes(-6);
            s.NotBefore = DateTime.UtcNow.AddMinutes(-6);
            s.Expires = DateTime.UtcNow.AddMinutes(-1);
        }), HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task Pass_just_past_expiry_is_accepted_within_the_clock_skew_and_rejected_beyond_it()
    {
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(s => { s.IssuedAt = DateTime.UtcNow.AddMinutes(-5); s.Expires = DateTime.UtcNow.AddSeconds(-20); s.Jti = "skew-ok"; })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ExchangeAsync(s => { s.IssuedAt = DateTime.UtcNow.AddMinutes(-5); s.Expires = DateTime.UtcNow.AddSeconds(-90); s.Jti = "skew-bad"; })).StatusCode);
    }

    [Fact]
    public async Task Skew_is_capped_at_sixty_seconds_whatever_is_configured()
    {
        await using var host = await StartAsync(o => o.Central.ClockSkewSeconds = 3600);

        var response = await ExchangeAsync(s => { s.IssuedAt = DateTime.UtcNow.AddMinutes(-5); s.Expires = DateTime.UtcNow.AddSeconds(-120); }, host);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Not_yet_valid_pass_is_rejected() =>
        await AssertErrorAsync(await ExchangeAsync(s =>
        {
            s.NotBefore = DateTime.UtcNow.AddMinutes(3);
            s.IssuedAt = DateTime.UtcNow;
        }), HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task Pass_issued_in_the_future_is_rejected() =>
        await AssertErrorAsync(await ExchangeAsync(s =>
        {
            s.IssuedAt = DateTime.UtcNow.AddMinutes(3);
            s.Expires = DateTime.UtcNow.AddMinutes(8);
        }), HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task Pass_with_an_unreasonably_long_life_is_rejected() =>
        await AssertErrorAsync(await ExchangeAsync(s => s.Expires = DateTime.UtcNow.AddHours(8)), HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task Bad_signature_is_rejected()
    {
        using var attacker = new CentralTestKeys(); // same kid, different private key
        var forged = attacker.Create();

        await AssertErrorAsync(await ExchangeRawAsync(forged), HttpStatusCode.Unauthorized, "invalid_pass");
    }

    [Fact]
    public async Task A_tampered_payload_is_rejected()
    {
        var parts = _keys.Create().Split('.');
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64UrlEncoder.Decode(parts[1]))!;
        var edited = payload.ToDictionary(p => p.Key, p => (object)p.Value);
        edited["role"] = "owner";
        var forged = parts[0] + "." + Base64UrlEncoder.Encode(JsonSerializer.Serialize(edited)) + "." + parts[2];

        await AssertErrorAsync(await ExchangeRawAsync(forged), HttpStatusCode.Unauthorized, "invalid_pass");
    }

    [Fact]
    public async Task Unknown_key_id_is_rejected() =>
        await AssertErrorAsync(await ExchangeAsync(s => s.KeyId = "not-a-published-key"), HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task Alg_none_is_rejected() =>
        await AssertErrorAsync(await ExchangeRawAsync(_keys.CreateUnsigned()), HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task Hs256_signed_with_the_public_key_as_the_secret_is_rejected() =>
        await AssertErrorAsync(await ExchangeRawAsync(_keys.CreateHs256WithPublicKeyAsSecret()), HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task Rs384_is_rejected_even_with_the_right_key()
    {
        var creds = new SigningCredentials(_keys.SigningKey, SecurityAlgorithms.RsaSha384);

        await AssertErrorAsync(await ExchangeRawAsync(_keys.Create(credentials: creds)), HttpStatusCode.Unauthorized, "invalid_pass");
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("email")]
    [InlineData("role")]
    [InlineData("jti")]
    [InlineData("exp")]
    [InlineData("iat")]
    [InlineData("nbf")]
    [InlineData("iss")]
    [InlineData("aud")]
    public async Task A_pass_missing_a_required_claim_is_rejected(string claim) =>
        await AssertErrorAsync(await ExchangeAsync(s => s.Omit.Add(claim)), HttpStatusCode.Unauthorized, "invalid_pass");

    [Theory]
    [InlineData("admin")]
    [InlineData("Owner")]
    [InlineData("")]
    public async Task A_pass_with_an_unknown_role_is_rejected(string role) =>
        await AssertErrorAsync(await ExchangeAsync(s => s.Role = role), HttpStatusCode.Unauthorized, "invalid_pass");

    [Fact]
    public async Task A_replayed_pass_is_rejected()
    {
        var pass = _keys.Create();

        Assert.Equal(HttpStatusCode.OK, (await ExchangeRawAsync(pass)).StatusCode);
        await AssertErrorAsync(await ExchangeRawAsync(pass), HttpStatusCode.Unauthorized, "invalid_pass");
    }

    [Fact]
    public async Task A_pass_is_not_a_session_token()
    {
        var pass = _keys.Create();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/auth/me", pass)).StatusCode);
    }

    [Fact]
    public async Task A_rejected_pass_opens_no_session_and_does_not_burn_its_id()
    {
        var pass = _keys.Create(s => s.Audience = "prj_someoneelses0001");
        await ExchangeRawAsync(pass);

        Assert.Equal(0, _host.Services.GetRequiredService<PassReplayCache>().Count);
    }

    // ---- malformed requests -------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"pass\":\"\"}")]
    [InlineData("{\"pass\":\"   \"}")]
    [InlineData("{\"pass\":42}")]
    [InlineData("{\"pass\":{\"x\":1}}")]
    public async Task Malformed_requests_get_invalid_request_not_framework_problem_details(string body)
    {
        var response = await _host.Client.PostAsync(Exchange, new StringContent(body, Encoding.UTF8, "application/json"));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Oversized_body_is_invalid_request()
    {
        var body = "{\"pass\":\"" + new string('a', 40 * 1024) + "\"}";
        var response = await _host.Client.PostAsync(Exchange, new StringContent(body, Encoding.UTF8, "application/json"));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Garbage_that_is_not_a_jwt_is_an_invalid_pass()
    {
        await AssertErrorAsync(await ExchangeRawAsync("this.is.not-a-jwt"), HttpStatusCode.Unauthorized, "invalid_pass");
        await AssertErrorAsync(await ExchangeRawAsync("one-part"), HttpStatusCode.Unauthorized, "invalid_pass");
    }

    // ---- central unreachable ------------------------------------------------------

    [Fact]
    public async Task Keys_unreachable_and_none_cached_is_503_central_unavailable()
    {
        _provider.Unavailable = true;

        await AssertErrorAsync(await ExchangeAsync(), HttpStatusCode.ServiceUnavailable, "central_unavailable");
    }

    [Fact]
    public async Task With_the_real_key_provider_cached_keys_keep_working_while_central_is_down_and_no_cache_means_503()
    {
        var jwks = new FakeJwksHandler { Body = _keys.JwksJson() };
        await using var warm = await StartWithRealProvider(jwks);
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(host: warm)).StatusCode);

        // Central goes away: a later pass (a known kid) is still checked against the cached key.
        jwks.Unreachable = true;
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(host: warm)).StatusCode);
        Assert.Equal(1, jwks.Requests);

        // A host that never managed to download the keys cannot judge the pass at all.
        var down = new FakeJwksHandler { Unreachable = true };
        await using var cold = await StartWithRealProvider(down);
        await AssertErrorAsync(await ExchangeAsync(host: cold), HttpStatusCode.ServiceUnavailable, "central_unavailable");
    }

    private Task<FootLookTestHost> StartWithRealProvider(FakeJwksHandler handler) =>
        FootLookTestHost.StartAsync(
            o =>
            {
                o.Central.ProjectId = CentralTestKeys.ProjectId;
                o.Central.Issuer = CentralTestKeys.Issuer;
            },
            services => services.AddSingleton<ICentralKeyProvider>(sp =>
                new CentralJwksKeyProvider(sp.GetRequiredService<FootLookOptions>().Central, new HttpClient(handler))));

    // ---- central mode vs local mode -----------------------------------------------

    [Theory]
    [InlineData("/footlook/auth/register")]
    [InlineData("/footlook/auth/login")]
    [InlineData("/footlook/auth/microsoft")]
    [InlineData("/footlook/auth/register/")]
    [InlineData("/FootLook/Auth/Login")]
    public async Task Central_mode_disables_the_local_account_routes(string path)
    {
        var response = await _host.PostAsync(path, body: new { email = "someone@example.invalid", password = FootLookTestHost.Password, idToken = "x", mode = "register", acceptedTerms = true });

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "disabled_in_central_mode");
    }

    [Theory]
    [InlineData("/footlook/auth/register")]
    [InlineData("/footlook/auth/login")]
    [InlineData("/footlook/auth/microsoft")]
    public async Task Central_mode_answers_404_even_to_a_malformed_body_and_other_verbs(string path)
    {
        var malformed = await _host.Client.PostAsync(path, new StringContent("{ not json", Encoding.UTF8, "application/json"));
        await AssertErrorAsync(malformed, HttpStatusCode.NotFound, "disabled_in_central_mode");

        var get = await _host.GetAsync(path);
        await AssertErrorAsync(get, HttpStatusCode.NotFound, "disabled_in_central_mode");
    }

    [Fact]
    public async Task Central_mode_does_not_create_an_account_through_register()
    {
        await _host.RegisterAsync("intruder@example.invalid");

        Assert.Null(_host.Services.GetRequiredService<IFootLookUserStore>().FindByEmail("intruder@example.invalid"));
    }

    [Fact]
    public async Task Without_a_project_id_local_sign_in_is_exactly_as_before()
    {
        await using var local = await FootLookTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.Created, (await local.RegisterAsync("dev@example.invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await local.LoginAsync("dev@example.invalid")).StatusCode);
        // Microsoft sign-in is still routed (no account store configured here -> its own 503, not 404).
        var microsoft = await local.PostAsync("/footlook/auth/microsoft", body: new { idToken = "x", mode = "login" });
        Assert.NotEqual(HttpStatusCode.NotFound, microsoft.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await local.GetAsync("/footlook/health")).StatusCode);
    }

    [Fact]
    public async Task Exchange_is_404_disabled_when_not_in_central_mode()
    {
        await using var local = await FootLookTestHost.StartAsync();

        var response = await local.PostAsync(Exchange, body: new { pass = _keys.Create() });

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "disabled");
    }

    [Fact]
    public async Task Config_in_central_mode_names_the_project_and_the_login_page()
    {
        var response = await _host.GetAsync("/footlook/auth/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await FootLookTestHost.ReadJsonAsync(response);
        Assert.Equal("central", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal(CentralTestKeys.ProjectId, doc.RootElement.GetProperty("projectId").GetString());
        Assert.Equal("https://www.footlook.co.za/connect", doc.RootElement.GetProperty("loginUrl").GetString());
        Assert.Equal(3, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task Config_uses_a_custom_login_url()
    {
        await using var host = await StartAsync(o => o.Central.LoginUrl = "http://localhost:4210/connect");

        using var doc = await FootLookTestHost.ReadJsonAsync(await host.GetAsync("/footlook/auth/config"));

        Assert.Equal("http://localhost:4210/connect", doc.RootElement.GetProperty("loginUrl").GetString());
    }

    [Fact]
    public async Task Config_in_local_mode_says_local_and_nothing_else()
    {
        await using var local = await FootLookTestHost.StartAsync();

        var response = await local.GetAsync("/footlook/auth/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await FootLookTestHost.ReadJsonAsync(response);
        Assert.Equal("local", doc.RootElement.GetProperty("mode").GetString());
        Assert.Single(doc.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task Config_and_exchange_need_no_token_but_protected_routes_still_do()
    {
        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync("/footlook/auth/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/captures")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetAsync("/footlook/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Options_bind_from_the_footlook_central_config_section_with_the_contract_defaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FootLook:Central:ProjectId"] = " prj_abc " })
            .Build();
        var options = new FootLookOptions();
        config.GetSection("FootLook").Bind(options);

        Assert.Equal("prj_abc", options.Central.ProjectId);
        Assert.True(options.Central.IsEnabled);
        Assert.Equal("https://footlook-auth.azurewebsites.net", options.Central.Issuer);
        Assert.Equal("https://footlook-auth.azurewebsites.net/.well-known/jwks.json", options.Central.JwksUrl);
        Assert.Equal("https://www.footlook.co.za/connect", options.Central.LoginUrl);
        Assert.Equal(60, options.Central.ClockSkewSeconds);
        Assert.False(new FootLookOptions().Central.IsEnabled);

        options.Central.Issuer = "http://localhost:5200/";
        Assert.Equal("http://localhost:5200/.well-known/jwks.json", options.Central.JwksUrl);
    }
}
