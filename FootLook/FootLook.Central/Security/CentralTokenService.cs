using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FootLook.Central.Options;
using FootLook.Core.Models;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Central.Security;

public sealed record IssuedToken(string Token, DateTime ExpiresAtUtc);

/// <summary>
/// Issues the two kinds of RS256 JWT the central service signs:
/// <list type="bullet">
/// <item>a central session token (aud = footlook-central), which only central's own APIs accept;</item>
/// <item>a pass (aud = the ProjectId), which only that project's host accepts.</item>
/// </list>
/// The audiences differ on purpose: neither token is valid where the other is expected.
/// </summary>
public sealed class CentralTokenService
{
    public const string SessionAudience = "footlook-central";

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan PassLifetime = TimeSpan.FromMinutes(5);

    private readonly ISigningKeyProvider _keys;
    private readonly CentralOptions _options;
    private readonly TimeProvider _time;

    public CentralTokenService(ISigningKeyProvider keys, CentralOptions options, TimeProvider time)
    {
        _keys = keys;
        _options = options;
        _time = time;
    }

    public async Task<IssuedToken> IssueSessionTokenAsync(FootLookUser user, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var expires = now + SessionLifetime;

        var claims = new Dictionary<string, object>
        {
            ["iss"] = _options.NormalizedIssuer,
            ["aud"] = SessionAudience,
            ["sub"] = SubjectOf(user),
            ["email"] = user.Email,
            ["name"] = user.DisplayName,
            ["isAdmin"] = user.IsAdmin,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["jti"] = NewJti(),
        };

        return new IssuedToken(await JwtCompact.SignAsync(_keys, claims, cancellationToken), FromUnix(expires.ToUnixTimeSeconds()));
    }

    public async Task<IssuedToken> IssuePassAsync(FootLookUser user, string projectId, string role, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var expires = now + PassLifetime;

        var claims = new Dictionary<string, object>
        {
            ["iss"] = _options.NormalizedIssuer,
            ["aud"] = projectId,
            ["sub"] = SubjectOf(user),
            ["email"] = user.Email,
            ["name"] = user.DisplayName,
            ["role"] = role,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["jti"] = NewJti(),
        };

        return new IssuedToken(await JwtCompact.SignAsync(_keys, claims, cancellationToken), FromUnix(expires.ToUnixTimeSeconds()));
    }

    /// <summary>The user id as the accounts store hands it out (32 hex digits, no dashes).</summary>
    private static string SubjectOf(FootLookUser user) =>
        Guid.TryParse(user.Id, out var id) ? id.ToString("N") : user.Id;

    private static string NewJti() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
}

/// <summary>Builds a compact JWS (RS256) and asks the key provider for the signature.</summary>
internal static class JwtCompact
{
    public static async Task<string> SignAsync(ISigningKeyProvider keys, IReadOnlyDictionary<string, object> claims, CancellationToken cancellationToken)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["kid"] = keys.KeyId,
        });
        var payload = JsonSerializer.SerializeToUtf8Bytes(claims);

        var signingInput = Base64UrlEncoder.Encode(header) + "." + Base64UrlEncoder.Encode(payload);
        var signature = await keys.SignAsync(Encoding.ASCII.GetBytes(signingInput), cancellationToken);
        return signingInput + "." + Base64UrlEncoder.Encode(signature);
    }
}
