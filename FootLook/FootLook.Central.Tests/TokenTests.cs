using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FootLook.Central.Security;
using FootLook.Core.Models;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Central.Tests;

/// <summary>What a host does with a pass: check it against the published keys, for its own project id only.</summary>
internal static class HostEmulator
{
    public static async Task<TokenValidationResult> ValidateAsync(string token, string jwksJson, string issuer, string audience)
    {
        var keys = new JsonWebKeySet(jwksJson).GetSigningKeys();
        return await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(60),
        });
    }

    public static async Task<string> JwksAsync(CentralFactory factory)
    {
        var response = await factory.GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    public static JsonElement Payload(string jwt)
    {
        var part = jwt.Split('.')[1];
        return JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(part)).RootElement;
    }

    public static JsonElement Header(string jwt)
    {
        var part = jwt.Split('.')[0];
        return JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(part)).RootElement;
    }
}

public class TokenTests : IDisposable
{
    private readonly CentralFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ---- central session token --------------------------------------------------------------------

    [Fact]
    public async Task A_session_token_carries_the_contract_claims_and_header()
    {
        var user = _factory.NewUser("alice", isAdmin: true);
        var before = DateTimeOffset.UtcNow;

        var token = await _factory.SessionAsync(user);

        var header = HostEmulator.Header(token);
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
        Assert.Equal(_factory.Keys.KeyId, header.GetProperty("kid").GetString());

        var p = HostEmulator.Payload(token);
        Assert.Equal(CentralFactory.Issuer, p.GetProperty("iss").GetString());
        Assert.Equal("footlook-central", p.GetProperty("aud").GetString());
        Assert.Equal(user.Id, p.GetProperty("sub").GetString());
        Assert.Equal("alice@example.invalid", p.GetProperty("email").GetString());
        Assert.Equal("alice", p.GetProperty("name").GetString());
        Assert.True(p.GetProperty("isAdmin").GetBoolean());
        var iat = p.GetProperty("iat").GetInt64();
        Assert.InRange(iat, before.ToUnixTimeSeconds() - 2, before.ToUnixTimeSeconds() + 5);
        Assert.Equal(iat + 8 * 3600, p.GetProperty("exp").GetInt64());
        Assert.Matches("^[0-9a-f]{32}$", p.GetProperty("jti").GetString()!);
        // A session token names no role and no project: it is not a pass.
        Assert.False(p.TryGetProperty("role", out _));
        Assert.False(p.TryGetProperty("nbf", out _));
    }

    [Fact]
    public async Task Session_tokens_are_signed_with_the_published_key_and_have_unique_ids()
    {
        var user = _factory.NewUser();
        var a = await _factory.SessionAsync(user);
        var b = await _factory.SessionAsync(user);

        var jwks = await HostEmulator.JwksAsync(_factory);
        var result = await HostEmulator.ValidateAsync(a, jwks, CentralFactory.Issuer, "footlook-central");

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.NotEqual(HostEmulator.Payload(a).GetProperty("jti").GetString(), HostEmulator.Payload(b).GetProperty("jti").GetString());
    }

    [Fact]
    public async Task Session_token_expiry_is_eight_hours_from_the_reported_time()
    {
        var issued = await _factory.Tokens.IssueSessionTokenAsync(_factory.NewUser());
        var exp = HostEmulator.Payload(issued.Token).GetProperty("exp").GetInt64();

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime, issued.ExpiresAtUtc);
        Assert.Equal(DateTimeKind.Utc, issued.ExpiresAtUtc.Kind);
    }

    // ---- pass ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_pass_carries_the_contract_claims()
    {
        var user = _factory.NewUser("bob");

        var issued = await _factory.Tokens.IssuePassAsync(user, "prj_abcdefghijklmnop", "member");

        var p = HostEmulator.Payload(issued.Token);
        Assert.Equal(CentralFactory.Issuer, p.GetProperty("iss").GetString());
        Assert.Equal("prj_abcdefghijklmnop", p.GetProperty("aud").GetString());
        Assert.Equal(user.Id, p.GetProperty("sub").GetString());
        Assert.Equal("bob@example.invalid", p.GetProperty("email").GetString());
        Assert.Equal("bob", p.GetProperty("name").GetString());
        Assert.Equal("member", p.GetProperty("role").GetString());
        var iat = p.GetProperty("iat").GetInt64();
        Assert.Equal(iat, p.GetProperty("nbf").GetInt64());
        Assert.Equal(iat + 300, p.GetProperty("exp").GetInt64());
        Assert.Matches("^[0-9a-f]{32}$", p.GetProperty("jti").GetString()!);
        Assert.False(p.TryGetProperty("isAdmin", out _));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(iat + 300).UtcDateTime, issued.ExpiresAtUtc);
    }

    [Fact]
    public async Task A_pass_validates_for_its_own_project_only_the_way_a_host_would_check_it()
    {
        var user = _factory.NewUser();
        var jwks = await HostEmulator.JwksAsync(_factory);
        var pass = (await _factory.Tokens.IssuePassAsync(user, "prj_aaaaaaaaaaaaaaaa", "owner")).Token;

        Assert.True((await HostEmulator.ValidateAsync(pass, jwks, CentralFactory.Issuer, "prj_aaaaaaaaaaaaaaaa")).IsValid);

        // Another project's host, a different issuer, and the central audience all reject it.
        Assert.False((await HostEmulator.ValidateAsync(pass, jwks, CentralFactory.Issuer, "prj_bbbbbbbbbbbbbbbb")).IsValid);
        Assert.False((await HostEmulator.ValidateAsync(pass, jwks, CentralFactory.Issuer, "footlook-central")).IsValid);
        Assert.False((await HostEmulator.ValidateAsync(pass, jwks, "https://someone-else.example", "prj_aaaaaaaaaaaaaaaa")).IsValid);
    }

    [Fact]
    public async Task Every_pass_has_its_own_single_use_id()
    {
        var user = _factory.NewUser();
        var ids = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            var pass = await _factory.Tokens.IssuePassAsync(user, "prj_aaaaaaaaaaaaaaaa", "owner");
            ids.Add(HostEmulator.Payload(pass.Token).GetProperty("jti").GetString()!);
        }

        Assert.Equal(50, ids.Count);
    }

    [Fact]
    public async Task A_pass_is_no_longer_valid_after_five_minutes_and_a_minute()
    {
        var jwks = await HostEmulator.JwksAsync(_factory);
        var user = _factory.NewUser();
        var stale = new CentralTokenService(_factory.Keys, new FootLook.Central.Options.CentralOptions { Issuer = CentralFactory.Issuer }, new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-7)));
        var justInside = new CentralTokenService(_factory.Keys, new FootLook.Central.Options.CentralOptions { Issuer = CentralFactory.Issuer }, new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-5)));

        Assert.False((await HostEmulator.ValidateAsync((await stale.IssuePassAsync(user, "prj_aaaaaaaaaaaaaaaa", "owner")).Token, jwks, CentralFactory.Issuer, "prj_aaaaaaaaaaaaaaaa")).IsValid);
        Assert.True((await HostEmulator.ValidateAsync((await justInside.IssuePassAsync(user, "prj_aaaaaaaaaaaaaaaa", "owner")).Token, jwks, CentralFactory.Issuer, "prj_aaaaaaaaaaaaaaaa")).IsValid);
    }

    // ---- audience separation ------------------------------------------------------------------------

    [Fact]
    public async Task A_session_token_is_not_a_pass_for_any_project()
    {
        var user = _factory.NewUser();
        var jwks = await HostEmulator.JwksAsync(_factory);
        var session = await _factory.SessionAsync(user);

        Assert.False((await HostEmulator.ValidateAsync(session, jwks, CentralFactory.Issuer, "prj_aaaaaaaaaaaaaaaa")).IsValid);
    }

    [Fact]
    public async Task A_pass_is_not_a_session_token_for_any_central_route()
    {
        var user = _factory.NewUser();
        var projectId = await _factory.CreateProjectAsync(user);
        var pass = (await _factory.Tokens.IssuePassAsync(user, projectId, "owner")).Token;

        foreach (var path in new[] { "/me", "/projects", $"/projects/{projectId}", $"/projects/{projectId}/members" })
        {
            await CentralFactory.AssertErrorAsync(await _factory.GetAsync(path, pass), HttpStatusCode.Unauthorized, "unauthorized");
        }

        await CentralFactory.AssertErrorAsync(await _factory.PostAsync($"/projects/{projectId}/connect", pass, new { returnUrl = "http://localhost:4200/" }), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task The_central_audience_is_refused_as_a_project_id_by_a_host_that_only_trusts_its_own()
    {
        // A pass whose aud happens to be footlook-central would work on central; central never issues one
        // (project ids are always prj_...), and hosts refuse any aud that is not their own project id.
        var user = _factory.NewUser();
        var session = await _factory.SessionAsync(user);
        var jwks = await HostEmulator.JwksAsync(_factory);
        Assert.False((await HostEmulator.ValidateAsync(session, jwks, CentralFactory.Issuer, "prj_zzzzzzzzzzzzzzzz")).IsValid);
    }

    // ---- forged and malformed tokens against the central APIs --------------------------------------------

    private static string Compact(object header, object payload, byte[]? signature = null) =>
        Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(header)) + "." +
        Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(payload)) + "." +
        (signature is null ? string.Empty : Base64UrlEncoder.Encode(signature));

    private object ValidClaims(FootLookUser user, long? exp = null, string? iss = null, string aud = "footlook-central") => new
    {
        iss = iss ?? CentralFactory.Issuer,
        aud,
        sub = user.Id,
        email = user.Email,
        name = user.DisplayName,
        isAdmin = false,
        iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        exp = exp ?? DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        jti = "x",
    };

    private async Task<string> SignAsync(object header, object claims, RSA key)
    {
        var input = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var sig = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        await Task.CompletedTask;
        return input + "." + Base64UrlEncoder.Encode(sig);
    }

    [Fact]
    public async Task Tokens_the_central_did_not_sign_are_refused()
    {
        var user = _factory.NewUser();
        using var attacker = RSA.Create(2048);
        var goodHeader = new { alg = "RS256", typ = "JWT", kid = _factory.Keys.KeyId };
        var real = await _factory.SessionAsync(user);
        var parts = real.Split('.');

        var forged = new Dictionary<string, string>
        {
            ["no signature at all (alg none)"] = Compact(new { alg = "none", typ = "JWT" }, ValidClaims(user)),
            ["alg none with the real kid"] = Compact(new { alg = "none", typ = "JWT", kid = _factory.Keys.KeyId }, ValidClaims(user)),
            ["HS256 keyed with the public modulus"] = Compact(new { alg = "HS256", typ = "JWT", kid = _factory.Keys.KeyId }, ValidClaims(user),
                HMACSHA256.HashData(_factory.Keys.PublicKeys[0].Modulus, Encoding.ASCII.GetBytes("x"))),
            ["signed by another key, real kid"] = await SignAsync(goodHeader, ValidClaims(user), attacker),
            ["signed by another key, unknown kid"] = await SignAsync(new { alg = "RS256", typ = "JWT", kid = "not-a-published-kid" }, ValidClaims(user), attacker),
            ["real token, tampered payload"] = parts[0] + "." + Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(ValidClaims(user, exp: DateTimeOffset.UtcNow.AddYears(5).ToUnixTimeSeconds()))) + "." + parts[2],
            ["real token, truncated signature"] = real[..^6],
            ["garbage"] = "garbage",
            ["two segments"] = parts[0] + "." + parts[1],
            ["empty segments"] = "..",
        };

        foreach (var (why, token) in forged)
        {
            var response = await _factory.GetAsync("/me", token);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{why}: expected 401, got {(int)response.StatusCode}");
            await CentralFactory.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        }
    }

    [Fact]
    public async Task Expired_wrong_issuer_and_missing_kid_tokens_are_refused_even_when_correctly_signed()
    {
        var user = _factory.NewUser();
        var header = new { alg = "RS256", typ = "JWT", kid = _factory.Keys.KeyId };

        // Sanity: the same construction with good claims is accepted, so the refusals below are down to the claims.
        var good = await SignAsync(header, ValidClaims(user), _factory.Rsa);
        Assert.Equal(HttpStatusCode.OK, (await _factory.GetAsync("/me", good)).StatusCode);

        var bad = new Dictionary<string, string>
        {
            ["expired 5 minutes ago"] = await SignAsync(header, ValidClaims(user, exp: DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()), _factory.Rsa),
            ["wrong issuer"] = await SignAsync(header, ValidClaims(user, iss: "https://evil.example"), _factory.Rsa),
            ["a project audience"] = await SignAsync(header, ValidClaims(user, aud: "prj_aaaaaaaaaaaaaaaa"), _factory.Rsa),
            ["no audience"] = await SignAsync(header, new { iss = CentralFactory.Issuer, sub = user.Id, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }, _factory.Rsa),
            ["no expiry"] = await SignAsync(header, new { iss = CentralFactory.Issuer, aud = "footlook-central", sub = user.Id }, _factory.Rsa),
            ["no kid"] = await SignAsync(new { alg = "RS256", typ = "JWT" }, ValidClaims(user), _factory.Rsa),
        };

        foreach (var (why, token) in bad)
        {
            var response = await _factory.GetAsync("/me", token);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{why}: expected 401, got {(int)response.StatusCode}");
        }

    }


    [Fact]
    public async Task The_token_is_only_read_from_the_authorization_header()
    {
        var token = await _factory.SessionAsync(_factory.NewUser());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.GetAsync($"/me?access_token={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.GetAsync($"/me?token={token}")).StatusCode);

        var withScheme = new HttpRequestMessage(HttpMethod.Get, "/me");
        withScheme.Headers.TryAddWithoutValidation("Authorization", "Basic " + token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().SendAsync(withScheme)).StatusCode);
    }

    // ---- rotation -----------------------------------------------------------------------------------

    [Fact]
    public async Task After_a_rotation_tokens_from_the_old_key_still_verify_and_new_ones_use_the_new_key()
    {
        using var oldRsa = RSA.Create(2048);
        using var newRsa = RSA.Create(2048);
        using var oldProvider = new LocalRsaSigningKeyProvider(oldRsa);
        var newProvider = new LocalRsaSigningKeyProvider(newRsa, oldProvider.PublicKeys);

        var user = _factory.NewUser();
        var oldTokens = new CentralTokenService(oldProvider, new FootLook.Central.Options.CentralOptions { Issuer = CentralFactory.Issuer }, TimeProvider.System);
        var oldSession = (await oldTokens.IssueSessionTokenAsync(user)).Token;
        var oldPass = (await oldTokens.IssuePassAsync(user, "prj_aaaaaaaaaaaaaaaa", "owner")).Token;

        await using var rotated = new RotatedApp(newProvider, _factory);
        var jwks = await rotated.JwksAsync();
        var keys = new JsonWebKeySet(jwks).Keys;

        // Two keys are published, the new one first; the old kid is still there.
        Assert.Equal(2, keys.Count);
        Assert.Equal(newProvider.KeyId, keys[0].Kid);
        Assert.Equal(oldProvider.KeyId, keys[1].Kid);

        // A host with the fresh JWKS accepts a pass signed before the rotation...
        Assert.True((await HostEmulator.ValidateAsync(oldPass, jwks, CentralFactory.Issuer, "prj_aaaaaaaaaaaaaaaa")).IsValid);
        // ...and central still accepts the old session token,
        Assert.Equal(HttpStatusCode.OK, (await rotated.GetAsync("/me", oldSession)).StatusCode);
        // while everything issued now names the new key.
        var fresh = await new CentralTokenService(newProvider, new FootLook.Central.Options.CentralOptions { Issuer = CentralFactory.Issuer }, TimeProvider.System).IssueSessionTokenAsync(user);
        Assert.Equal(newProvider.KeyId, HostEmulator.Header(fresh.Token).GetProperty("kid").GetString());
        Assert.Equal(HttpStatusCode.OK, (await rotated.GetAsync("/me", fresh.Token)).StatusCode);
    }

    [Fact]
    public async Task A_key_that_was_dropped_from_the_published_set_no_longer_verifies()
    {
        using var oldRsa = RSA.Create(2048);
        using var newRsa = RSA.Create(2048);
        using var oldProvider = new LocalRsaSigningKeyProvider(oldRsa);
        var newProvider = new LocalRsaSigningKeyProvider(newRsa); // rotation finished: the old key is gone

        var user = _factory.NewUser();
        var oldSession = (await new CentralTokenService(oldProvider, new FootLook.Central.Options.CentralOptions { Issuer = CentralFactory.Issuer }, TimeProvider.System).IssueSessionTokenAsync(user)).Token;

        await using var rotated = new RotatedApp(newProvider, _factory);
        Assert.Single(new JsonWebKeySet(await rotated.JwksAsync()).Keys);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rotated.GetAsync("/me", oldSession)).StatusCode);
    }

    /// <summary>The same app, with a different signing key provider and sharing the first app's accounts.</summary>
    private sealed class RotatedApp : IAsyncDisposable
    {
        private readonly CentralFactory _inner;

        public RotatedApp(ISigningKeyProvider provider, CentralFactory original)
        {
            _inner = new CentralFactory(f =>
            {
                f.SigningProvider = provider;
            });
            // Same accounts, so the user the token names exists here too.
            foreach (var user in original.Accounts.Users)
            {
                _inner.Accounts.Add(user);
            }
        }

        public async Task<string> JwksAsync() => await HostEmulator.JwksAsync(_inner);

        public Task<HttpResponseMessage> GetAsync(string path, string token) => _inner.GetAsync(path, token);

        public ValueTask DisposeAsync()
        {
            _inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
