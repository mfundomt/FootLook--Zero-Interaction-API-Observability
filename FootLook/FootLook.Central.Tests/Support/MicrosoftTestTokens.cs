using System.Security.Cryptography;
using FootLook.Core.Models;
using FootLook.Core.Options;
using FootLook.Core.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Central.Tests;

/// <summary>Knobs for one test ID token; defaults produce a valid v2.0 work-account token.</summary>
public sealed class TokenSpec
{
    public string? Tid { get; set; } = MicrosoftTestTokens.WorkTenant;
    public string? Oid { get; set; } = "11111111-2222-3333-4444-555555555555";
    public string? Sub { get; set; } = "AAAA-pairwise-subject";
    public string? Email { get; set; } = "dev@example.invalid";
    public string? PreferredUsername { get; set; }
    public string? Name { get; set; } = "Dev Person";
    public string? Ver { get; set; } = "2.0";
    public string Audience { get; set; } = MicrosoftTestTokens.ClientId;

    /// <summary>Null means the correct issuer for <see cref="Tid"/>.</summary>
    public string? Issuer { get; set; }

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime NotBefore { get; set; } = DateTime.UtcNow.AddSeconds(-5);
    public DateTime Expires { get; set; } = DateTime.UtcNow.AddMinutes(50);
}

/// <summary>
/// Mints Microsoft-style ID tokens signed with a locally generated RSA key and builds a
/// validator that trusts exactly that key, so nothing here talks to login.microsoftonline.com.
/// </summary>
public sealed class MicrosoftTestTokens : IDisposable
{
    public const string ClientId = "264de9b0-15e7-4887-b686-5b6c18a2f376";
    public const string WorkTenant = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    public const string KeyId = "test-key-1";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly JsonWebTokenHandler _handler = new();

    public SigningCredentials Credentials { get; }

    public MicrosoftTestTokens()
    {
        Credentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256);
    }

    public MicrosoftIdTokenValidator CreateValidator(Action<FootLookMicrosoftOptions>? configure = null)
    {
        var options = new FootLookMicrosoftOptions { ClientId = ClientId };
        configure?.Invoke(options);

        var configuration = new OpenIdConnectConfiguration();
        configuration.SigningKeys.Add(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = KeyId });

        return new MicrosoftIdTokenValidator(options, null, new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration));
    }

    public string Create(Action<TokenSpec>? configure = null, SigningCredentials? credentials = null)
    {
        var spec = new TokenSpec();
        configure?.Invoke(spec);

        var claims = new Dictionary<string, object>();
        void Add(string name, string? value)
        {
            if (value is not null)
            {
                claims[name] = value;
            }
        }

        Add("tid", spec.Tid);
        Add("oid", spec.Oid);
        Add("sub", spec.Sub);
        Add("email", spec.Email);
        Add("preferred_username", spec.PreferredUsername);
        Add("name", spec.Name);
        Add("ver", spec.Ver);

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = spec.Issuer ?? MicrosoftIdTokenValidator.ExpectedIssuer(spec.Tid ?? string.Empty),
            Audience = spec.Audience,
            IssuedAt = spec.IssuedAt,
            NotBefore = spec.NotBefore,
            Expires = spec.Expires,
            Claims = claims,
            SigningCredentials = credentials ?? Credentials,
        });
    }

    public void Dispose() => _rsa.Dispose();
}

