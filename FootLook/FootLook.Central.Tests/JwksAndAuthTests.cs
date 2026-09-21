using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FootLook.Central.Security;
using FootLook.Core.Security;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Central.Tests;

public class JwksEndpointTests : IDisposable
{
    private readonly CentralFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Jwks_is_anonymous_public_json_with_the_documented_shape()
    {
        var response = await _factory.GetAsync("/.well-known/jwks.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=3600", response.Headers.CacheControl?.ToString());

        using var doc = await CentralFactory.JsonAsync(response);
        Assert.Equal(new[] { "keys" }, doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        var key = Assert.Single(doc.RootElement.GetProperty("keys").EnumerateArray());
        Assert.Equal(new[] { "kty", "use", "alg", "kid", "n", "e" }, key.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("RSA", key.GetProperty("kty").GetString());
        Assert.Equal("sig", key.GetProperty("use").GetString());
        Assert.Equal("RS256", key.GetProperty("alg").GetString());
        Assert.Equal(_factory.Keys.KeyId, key.GetProperty("kid").GetString());
        Assert.Equal("AQAB", key.GetProperty("e").GetString());
        Assert.Equal(256, Base64UrlEncoder.DecodeBytes(key.GetProperty("n").GetString()!).Length);
    }

    [Fact]
    public async Task Jwks_never_contains_private_key_parameters()
    {
        var raw = await (await _factory.GetAsync("/.well-known/jwks.json")).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var names = doc.RootElement.GetProperty("keys")[0].EnumerateObject().Select(p => p.Name).ToHashSet();

        foreach (var privateName in new[] { "d", "p", "q", "dp", "dq", "qi", "k", "oth" })
        {
            Assert.DoesNotContain(privateName, names);
        }

        // And nothing derived from the private numbers appears as text anywhere.
        var parameters = _factory.Rsa.ExportParameters(true);
        Assert.DoesNotContain(Base64UrlEncoder.Encode(parameters.D!), raw);
        Assert.DoesNotContain(Base64UrlEncoder.Encode(parameters.P!), raw);
    }

    [Fact]
    public async Task Jwks_loads_in_the_standard_library_and_verifies_a_signature_made_by_the_service()
    {
        var jwks = new JsonWebKeySet(await HostEmulator.JwksAsync(_factory));
        var token = await _factory.SessionAsync(_factory.NewUser());

        var result = await HostEmulator.ValidateAsync(token, await HostEmulator.JwksAsync(_factory), CentralFactory.Issuer, "footlook-central");

        Assert.Single(jwks.Keys);
        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://www.footlook.co.za")]
    [InlineData(null)]
    public async Task Jwks_is_open_to_every_origin(string? origin)
    {
        var response = await _factory.SendAsync(HttpMethod.Get, "/.well-known/jwks.json", origin: origin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (origin is not null)
        {
            Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        }

        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Jwks_preflight_from_any_origin_is_answered()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/.well-known/jwks.json");
        request.Headers.Add("Origin", "https://anyone.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Jwks_only_answers_get()
    {
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _factory.PostAsync("/.well-known/jwks.json")).StatusCode);
    }

    [Fact]
    public async Task With_two_keys_both_are_published_current_first()
    {
        using var older = RSA.Create(2048);
        using var current = RSA.Create(2048);
        var previous = new LocalRsaSigningKeyProvider(older).PublicKeys;
        using var provider = new LocalRsaSigningKeyProvider(current, previous);
        using var factory = new CentralFactory(f => f.SigningProvider = provider);

        using var doc = await CentralFactory.JsonAsync(await factory.GetAsync("/.well-known/jwks.json"));
        var kids = doc.RootElement.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToArray();

        Assert.Equal(new[] { provider.KeyId, previous[0].KeyId }, kids);
    }

    [Fact]
    public async Task A_deployed_instance_takes_its_key_from_configuration()
    {
        using var rsa = RSA.Create(2048);
        using var local = LocalRsaSigningKeyProvider.FromPem(rsa.ExportPkcs8PrivateKeyPem());
        var expected = local.KeyId;

        using var factory = new CentralFactory(f =>
        {
            f.EnvironmentName = "Production";
            f.UseTestSigningKey = false;
            f.Settings["Central:SigningKeyPem"] = rsa.ExportPkcs8PrivateKeyPem();
        });

        using var doc = await CentralFactory.JsonAsync(await factory.GetAsync("/.well-known/jwks.json"));
        Assert.Equal(expected, doc.RootElement.GetProperty("keys")[0].GetProperty("kid").GetString());
    }

    [Fact]
    public async Task Rotation_can_be_configured_with_earlier_public_keys()
    {
        using var current = RSA.Create(2048);
        using var older = RSA.Create(2048);

        using var factory = new CentralFactory(f =>
        {
            f.EnvironmentName = "Production";
            f.UseTestSigningKey = false;
            f.Settings["Central:SigningKeyPem"] = current.ExportPkcs8PrivateKeyPem();
            f.Settings["Central:PreviousSigningKeyPems:0"] = older.ExportSubjectPublicKeyInfoPem();
        });

        using var doc = await CentralFactory.JsonAsync(await factory.GetAsync("/.well-known/jwks.json"));
        Assert.Equal(2, doc.RootElement.GetProperty("keys").GetArrayLength());
    }

    [Fact]
    public void Outside_development_startup_fails_clearly_without_a_signing_key()
    {
        using var factory = new CentralFactory(f =>
        {
            f.EnvironmentName = "Production";
            f.UseTestSigningKey = false;
        });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var messages = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        Assert.Contains(messages, m => m.Contains("No signing key is configured") && m.Contains("Central:SigningKeyPem"));
    }

    [Fact]
    public async Task Development_without_a_key_starts_with_a_throwaway_key_and_warns()
    {
        using var factory = new CentralFactory(f => f.UseTestSigningKey = false);

        Assert.Equal(HttpStatusCode.OK, (await factory.GetAsync("/.well-known/jwks.json")).StatusCode);
        Assert.Contains(factory.Logs.Lines, l => l.StartsWith("Warning") && l.Contains("throwaway"));
    }
}

/// <summary>POST /auth/microsoft and GET /me: the sign-in contract the site already uses, with the central token.</summary>
public class AuthEndpointTests : IDisposable
{
    private const string Path = "/auth/microsoft";

    private readonly CentralFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private Task<HttpResponseMessage> SignInAsync(string mode, Action<TokenSpec>? spec = null, bool? acceptedTerms = true, CentralFactory? on = null)
    {
        var f = on ?? _factory;
        return f.PostAsync(Path, body: new { idToken = f.MsTokens.Create(spec), mode, acceptedTerms });
    }

    // ---- success ------------------------------------------------------------------------------

    [Fact]
    public async Task Register_new_account_returns_200_with_the_contract_shape()
    {
        var response = await SignInAsync("register");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.Equal(new[] { "token", "expiresAtUtc", "isAdmin", "user" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("token").GetString()));
        Assert.True(root.GetProperty("expiresAtUtc").GetDateTime() > DateTime.UtcNow.AddHours(7));
        Assert.EndsWith("Z", root.GetProperty("expiresAtUtc").GetString());
        Assert.True(root.GetProperty("isAdmin").GetBoolean());

        var user = root.GetProperty("user");
        Assert.Equal("dev@example.invalid", user.GetProperty("email").GetString());
        Assert.Equal("Dev Person", user.GetProperty("displayName").GetString());
        Assert.Equal(3, user.EnumerateObject().Count());
        Assert.Equal(Assert.Single(_factory.Accounts.Users).Id, user.GetProperty("id").GetString());
    }

    [Fact]
    public async Task The_token_is_a_central_session_token_that_works_on_central_routes_and_carries_the_account()
    {
        using var doc = await CentralFactory.JsonAsync(await SignInAsync("register"));
        var token = doc.RootElement.GetProperty("token").GetString()!;
        var userId = doc.RootElement.GetProperty("user").GetProperty("id").GetString();

        var p = HostEmulator.Payload(token);
        Assert.Equal("footlook-central", p.GetProperty("aud").GetString());
        Assert.Equal(CentralFactory.Issuer, p.GetProperty("iss").GetString());
        Assert.Equal(userId, p.GetProperty("sub").GetString());
        Assert.True(p.GetProperty("isAdmin").GetBoolean());

        var me = await _factory.GetAsync("/me", token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var meDoc = await CentralFactory.JsonAsync(me);
        Assert.Equal(new[] { "id", "email", "displayName", "isAdmin" }, meDoc.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.Equal(userId, meDoc.RootElement.GetProperty("id").GetString());
        Assert.Equal("dev@example.invalid", meDoc.RootElement.GetProperty("email").GetString());
        Assert.Equal("Dev Person", meDoc.RootElement.GetProperty("displayName").GetString());
        Assert.True(meDoc.RootElement.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task The_microsoft_id_token_is_not_accepted_as_a_central_session_token()
    {
        var idToken = _factory.MsTokens.Create();

        await CentralFactory.AssertErrorAsync(await _factory.GetAsync("/me", idToken), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task First_account_is_admin_and_the_next_is_not()
    {
        using var first = await CentralFactory.JsonAsync(await SignInAsync("register", s => s.Oid = "first"));
        using var second = await CentralFactory.JsonAsync(await SignInAsync("register", s => s.Oid = "second"));

        Assert.True(first.RootElement.GetProperty("isAdmin").GetBoolean());
        Assert.False(second.RootElement.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task Login_for_an_existing_account_returns_200_and_records_a_login()
    {
        await SignInAsync("register");

        var response = await SignInAsync("login", acceptedTerms: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_factory.Accounts.Users);
        Assert.Equal(new[] { "register", "login" }, _factory.Accounts.Outcomes.ToArray());
    }

    [Fact]
    public async Task Register_for_an_identity_that_already_exists_just_signs_in()
    {
        using var first = await CentralFactory.JsonAsync(await SignInAsync("register"));
        using var again = await CentralFactory.JsonAsync(await SignInAsync("register"));

        Assert.Equal(
            first.RootElement.GetProperty("user").GetProperty("id").GetString(),
            again.RootElement.GetProperty("user").GetProperty("id").GetString());
        Assert.Single(_factory.Accounts.Users);
    }

    [Fact]
    public async Task Same_email_with_a_different_tenant_or_subject_is_a_different_account()
    {
        using var a = await CentralFactory.JsonAsync(await SignInAsync("register", s => s.Oid = "subject-a"));
        using var b = await CentralFactory.JsonAsync(await SignInAsync("register", s => s.Oid = "subject-b"));
        using var c = await CentralFactory.JsonAsync(await SignInAsync("register", s => { s.Tid = MicrosoftIdTokenValidator.PersonalTenantId; s.Oid = "subject-a"; }));

        var ids = new[] { a, b, c }.Select(d => d.RootElement.GetProperty("user").GetProperty("id").GetString()).ToArray();
        Assert.Equal(3, ids.Distinct().Count());
    }

    // ---- error contract (the same codes FootLook.Core's endpoint answers) -----------------------------

    [Fact]
    public async Task Login_when_no_account_exists_is_404_account_not_found()
    {
        await CentralFactory.AssertErrorAsync(await SignInAsync("login", acceptedTerms: false), HttpStatusCode.NotFound, "account_not_found");
        Assert.Empty(_factory.Accounts.Users);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Register_without_accepted_terms_is_400_terms_not_accepted(bool? accepted)
    {
        await CentralFactory.AssertErrorAsync(await SignInAsync("register", acceptedTerms: accepted), HttpStatusCode.BadRequest, "terms_not_accepted");
        Assert.Equal(0, _factory.Accounts.SignInCalls);
    }

    [Fact]
    public async Task Terms_are_not_required_for_login()
    {
        await SignInAsync("register");

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync("login", acceptedTerms: null)).StatusCode);
    }

    [Fact]
    public async Task Invalid_microsoft_token_is_401_invalid_token_without_detail()
    {
        var tokens = new[]
        {
            "garbage", "a.b.c",
            _factory.MsTokens.Create(s => { s.NotBefore = DateTime.UtcNow.AddHours(-3); s.Expires = DateTime.UtcNow.AddHours(-1); }),
            _factory.MsTokens.Create(s => s.Audience = "someone-else"),
            _factory.MsTokens.Create(s => s.Issuer = "https://login.microsoftonline.com/other/v2.0"),
        };

        foreach (var idToken in tokens)
        {
            var response = await _factory.PostAsync(Path, body: new { idToken, mode = "login", acceptedTerms = false });
            await CentralFactory.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "invalid_token");
        }

        Assert.Equal(0, _factory.Accounts.SignInCalls);
    }

    [Fact]
    public async Task Register_of_a_new_account_when_registration_is_closed_is_403_registration_closed()
    {
        using var closed = new CentralFactory(f => f.Settings["Central:AllowRegistration"] = "false");

        await CentralFactory.AssertErrorAsync(await SignInAsync("register", on: closed), HttpStatusCode.Forbidden, "registration_closed");
        Assert.Empty(closed.Accounts.Users);
    }

    [Fact]
    public async Task Existing_accounts_can_still_sign_in_when_registration_is_closed_and_login_of_an_unknown_one_is_not_found()
    {
        using var closed = new CentralFactory(f => f.Settings["Central:AllowRegistration"] = "false");
        closed.Accounts.SignInAsync(new FootLook.Core.Models.MicrosoftIdentity(MicrosoftTestTokens.WorkTenant, "11111111-2222-3333-4444-555555555555", "dev@example.invalid", "Dev Person", "work"), true, true, null, null).GetAwaiter().GetResult();

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync("register", on: closed)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync("login", acceptedTerms: false, on: closed)).StatusCode);
        await CentralFactory.AssertErrorAsync(await SignInAsync("login", s => s.Oid = "unknown", false, on: closed), HttpStatusCode.NotFound, "account_not_found");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("{\"idToken\":\"x\"}")]
    [InlineData("{\"mode\":\"login\"}")]
    [InlineData("{\"idToken\":\"\",\"mode\":\"login\"}")]
    [InlineData("{\"idToken\":\"x\",\"mode\":\"delete\"}")]
    [InlineData("{\"idToken\":\"x\",\"mode\":\"login\",\"acceptedTerms\":\"yes\"}")]
    [InlineData("{\"idToken\":12,\"mode\":\"login\"}")]
    public async Task Malformed_requests_are_400_invalid_request(string body)
    {
        var response = await _factory.SendAsync(HttpMethod.Post, Path, rawBody: body);

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Missing_body_is_400_invalid_request()
    {
        await CentralFactory.AssertErrorAsync(await _factory.SendAsync(HttpMethod.Post, Path), HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Oversized_body_is_400_invalid_request()
    {
        var body = "{\"idToken\":\"" + new string('a', 40_000) + "\",\"mode\":\"login\"}";

        await CentralFactory.AssertErrorAsync(await _factory.SendAsync(HttpMethod.Post, Path, rawBody: body), HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Without_an_account_store_the_endpoint_answers_503_accounts_unavailable()
    {
        using var noStore = new CentralFactory(f => f.UseFakeStores = false);

        await CentralFactory.AssertErrorAsync(await SignInAsync("register", on: noStore), HttpStatusCode.ServiceUnavailable, "accounts_unavailable");
        // A bad token is still just a bad token, whatever the store situation.
        var garbage = await noStore.PostAsync(Path, body: new { idToken = "garbage", mode = "login" });
        await CentralFactory.AssertErrorAsync(garbage, HttpStatusCode.Unauthorized, "invalid_token");
    }

    [Fact]
    public async Task A_failing_store_is_503_accounts_unavailable_and_leaks_nothing()
    {
        _factory.Accounts.ThrowOnSignIn = new InvalidOperationException("Server=secret.example;Password=hunter2");

        var response = await SignInAsync("register");

        await CentralFactory.AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "accounts_unavailable");
        Assert.DoesNotContain("hunter2", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(_factory.Logs.Lines, l => l.Contains("hunter2") || l.Contains("secret.example"));
    }

    [Fact]
    public async Task Route_only_accepts_post()
    {
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _factory.GetAsync(Path)).StatusCode);
    }

    // ---- /me --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Me_needs_a_token()
    {
        await CentralFactory.AssertErrorAsync(await _factory.GetAsync("/me"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Me_reads_the_account_fresh_and_not_from_the_token()
    {
        var user = _factory.NewUser("carol");
        var token = await _factory.SessionAsync(user);
        _factory.Accounts.Rename(user.Id, "Carol Renamed");

        using var doc = await CentralFactory.JsonAsync(await _factory.GetAsync("/me", token));

        Assert.Equal("Carol Renamed", doc.RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Me_for_a_deleted_account_is_401()
    {
        var user = _factory.NewUser();
        var token = await _factory.SessionAsync(user);
        _factory.Accounts.Remove(user.Id);

        await CentralFactory.AssertErrorAsync(await _factory.GetAsync("/me", token), HttpStatusCode.Unauthorized, "unauthorized");
    }
}
