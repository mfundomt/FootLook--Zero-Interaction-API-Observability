using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FootLook.Core.Options;
using FootLook.Core.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Tests;

/// <summary>Knobs for one test pass; defaults produce a valid member pass for <see cref="CentralTestKeys.ProjectId"/>.</summary>
public sealed class PassSpec
{
    public string? Issuer { get; set; } = CentralTestKeys.Issuer;
    public string? Audience { get; set; } = CentralTestKeys.ProjectId;
    public string? Subject { get; set; } = "0f8e6a52-3c1d-4e0b-9a67-0c5d2f1b7a11";
    public string? Email { get; set; } = "dev@example.invalid";
    public string? Name { get; set; } = "Dev Person";
    public string? Role { get; set; } = "member";
    public string? Jti { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime NotBefore { get; set; } = DateTime.UtcNow.AddSeconds(-5);
    public DateTime Expires { get; set; } = DateTime.UtcNow.AddMinutes(5);
    public string KeyId { get; set; } = CentralTestKeys.KeyId;

    /// <summary>Claim names left out of the payload entirely.</summary>
    public HashSet<string> Omit { get; } = new();

    /// <summary>Extra payload members (for example a second audience).</summary>
    public Dictionary<string, object> Extra { get; } = new();
}

/// <summary>
/// Mints passes the way the central service would - RS256, signed with a locally generated RSA
/// key, kid in the header - and exposes that key as a JWKS document and as an
/// <see cref="ICentralKeyProvider"/>, so nothing here talks to the real service.
/// </summary>
public sealed class CentralTestKeys : IDisposable
{
    public const string Issuer = "https://central.test";
    public const string ProjectId = "prj_testproject0001";
    public const string KeyId = "central-key-1";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly JsonWebTokenHandler _handler = new();

    public SigningCredentials Credentials { get; }

    public CentralTestKeys(string keyId = KeyId)
    {
        CurrentKeyId = keyId;
        Credentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
    }

    public string CurrentKeyId { get; }

    /// <summary>The key pair including the private half (for signing with a different algorithm).</summary>
    public RsaSecurityKey SigningKey => new(_rsa) { KeyId = CurrentKeyId };

    public RsaSecurityKey PublicKey => new(_rsa.ExportParameters(false)) { KeyId = CurrentKeyId };

    /// <summary>A JWKS document as central serves it, with any number of extra RSA public keys.</summary>
    public string JwksJson(params CentralTestKeys[] others) =>
        JsonSerializer.Serialize(new { keys = new[] { this }.Concat(others).Select(k => k.JwkEntry()).ToArray() });

    public object JwkEntry(string? alg = "RS256", string? use = "sig")
    {
        var p = _rsa.ExportParameters(false);
        var entry = new Dictionary<string, object?>
        {
            ["kty"] = "RSA",
            ["kid"] = CurrentKeyId,
            ["n"] = Base64UrlEncoder.Encode(p.Modulus!),
            ["e"] = Base64UrlEncoder.Encode(p.Exponent!),
        };
        if (use is not null) entry["use"] = use;
        if (alg is not null) entry["alg"] = alg;
        return entry;
    }

    public StaticKeyProvider Provider() => new(this);

    /// <summary>Applies <paramref name="configure"/>, then signs with this key (or <paramref name="credentials"/>).</summary>
    public string Create(Action<PassSpec>? configure = null, SigningCredentials? credentials = null)
    {
        var spec = new PassSpec();
        configure?.Invoke(spec);

        var payload = new Dictionary<string, object>();
        void Add(string name, object? value)
        {
            if (value is not null && !spec.Omit.Contains(name))
            {
                payload[name] = value;
            }
        }

        static long Seconds(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds();

        Add("iss", spec.Issuer);
        Add("aud", spec.Audience);
        Add("sub", spec.Subject);
        Add("email", spec.Email);
        Add("name", spec.Name);
        Add("role", spec.Role);
        Add("jti", spec.Jti);
        Add("iat", Seconds(spec.IssuedAt));
        Add("nbf", Seconds(spec.NotBefore));
        Add("exp", Seconds(spec.Expires));
        foreach (var (key, value) in spec.Extra)
        {
            payload[key] = value;
        }

        var creds = credentials ?? new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = spec.KeyId }, SecurityAlgorithms.RsaSha256);
        return _handler.CreateToken(JsonSerializer.Serialize(payload), creds);
    }

    /// <summary>A pass signed with a symmetric key made from this key's public modulus (the classic algorithm-confusion trick).</summary>
    public string CreateHs256WithPublicKeyAsSecret(Action<PassSpec>? configure = null)
    {
        var secret = _rsa.ExportParameters(false).Modulus!;
        var creds = new SigningCredentials(new SymmetricSecurityKey(secret) { KeyId = CurrentKeyId }, SecurityAlgorithms.HmacSha256);
        return Create(configure, creds);
    }

    /// <summary>An unsigned token that claims alg "none", with this key's kid.</summary>
    public string CreateUnsigned(Action<PassSpec>? configure = null)
    {
        var signed = Create(configure);
        var payload = signed.Split('.')[1];
        var header = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes($"{{\"alg\":\"none\",\"typ\":\"JWT\",\"kid\":\"{CurrentKeyId}\"}}"));
        return header + "." + payload + ".";
    }

    public void Dispose() => _rsa.Dispose();
}

/// <summary>Serves fixed keys, or reports the service as unreachable.</summary>
public sealed class StaticKeyProvider : ICentralKeyProvider
{
    private readonly Dictionary<string, SecurityKey> _keys = new();

    public bool Unavailable { get; set; }
    public int Lookups { get; private set; }

    public StaticKeyProvider(params CentralTestKeys[] keys)
    {
        foreach (var k in keys)
        {
            _keys[k.CurrentKeyId] = k.PublicKey;
        }
    }

    public Task<CentralKeyLookup> GetKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        Lookups++;
        if (Unavailable)
        {
            return Task.FromResult(new CentralKeyLookup(null, Unavailable: true));
        }

        return Task.FromResult(new CentralKeyLookup(_keys.GetValueOrDefault(keyId)));
    }
}

/// <summary>Stands in for the central service's JWKS address: counts requests and can fail on demand.</summary>
public sealed class FakeJwksHandler : HttpMessageHandler
{
    public string Body { get; set; } = "{\"keys\":[]}";
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public bool Unreachable { get; set; }
    public int Requests { get; private set; }
    public Uri? LastUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        LastUri = request.RequestUri;
        if (Unreachable)
        {
            throw new HttpRequestException("central is unreachable (test)");
        }

        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
    }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public static class CentralTestOptions
{
    public static FootLookCentralOptions Central(string? jwksUrl = null) => new()
    {
        ProjectId = CentralTestKeys.ProjectId,
        Issuer = CentralTestKeys.Issuer,
        JwksUrl = jwksUrl ?? "https://central.test/.well-known/jwks.json",
    };
}
