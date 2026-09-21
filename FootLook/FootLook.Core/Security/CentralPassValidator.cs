using FootLook.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Core.Security
{
    /// <summary>The identity a valid pass proves. Contains only what the pass itself carried.</summary>
    /// <param name="Subject">The central account id (the pass's sub claim).</param>
    /// <param name="Role">"owner" or "member".</param>
    /// <param name="JwtId">The pass's unique id; a pass may be used once.</param>
    /// <param name="ExpiresAtUtc">When the pass stops being valid (before clock-skew tolerance).</param>
    public sealed record CentralPass(
        string Subject,
        string Email,
        string DisplayName,
        string Role,
        string JwtId,
        DateTimeOffset ExpiresAtUtc)
    {
        public bool IsOwner => string.Equals(Role, CentralPassValidator.OwnerRole, StringComparison.Ordinal);
    }

    /// <summary>Outcome of <see cref="IPassValidator.ValidateAsync"/>.</summary>
    /// <param name="Pass">The proven identity, or null when the pass was rejected.</param>
    /// <param name="Failure">Short reason code for logs and tests - never sent to clients.</param>
    /// <param name="Unavailable">True when the pass could not be checked at all (central's keys could not be loaded), as opposed to being bad.</param>
    public sealed record PassValidationResult(CentralPass? Pass, string? Failure, bool Unavailable = false)
    {
        public bool IsValid => Pass is not null;

        public static PassValidationResult Ok(CentralPass pass) => new(pass, null);

        public static PassValidationResult Reject(string reason) => new(null, reason);
    }

    /// <summary>
    /// Checks a pass from FootLook's central sign-in. Register your own before AddFootLook to
    /// replace the default (<see cref="CentralPassValidator"/>), e.g. in a test.
    /// </summary>
    public interface IPassValidator
    {
        Task<PassValidationResult> ValidateAsync(string? pass, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Validates a pass (an RS256 JWT signed by the central service) for this host's project:
    /// <list type="bullet">
    /// <item>the header must say alg RS256 (anything else - none, HS256, RS384, ... - is rejected
    /// before any key is used, so a public key can never be reinterpreted as an HMAC secret) and name
    /// a kid, and the signature must verify with the JWKS key of that kid;</item>
    /// <item>iss must equal the configured issuer and aud must be exactly one value, equal to the
    /// configured ProjectId (a pass minted for another project is therefore worthless here);</item>
    /// <item>exp, nbf and iat must be present and consistent with the clock (skew from the options,
    /// at most 60 s), and the pass may not live longer than <see cref="MaxLifetime"/>;</item>
    /// <item>sub, email, role (owner|member) and jti must be present; name is optional.</item>
    /// </list>
    /// Single use of the jti is NOT checked here (see <see cref="PassReplayCache"/>).
    /// Uses only Microsoft.IdentityModel.* packages that Core already pulls in through JwtBearer.
    /// </summary>
    public sealed class CentralPassValidator : IPassValidator
    {
        public const string OwnerRole = "owner";
        public const string MemberRole = "member";

        /// <summary>Longest exp - iat accepted. Central issues 5 minutes; this is a generous ceiling.</summary>
        public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

        private const int MaxTokenLength = 8 * 1024;
        private const int MaxKeyIdLength = 128;
        private const int MaxSubjectLength = 128;
        private const int MaxEmailLength = 320;
        private const int MaxDisplayNameLength = 200;
        private const int MaxJwtIdLength = 128;
        private const int MaxClockSkewSeconds = 60;

        private readonly FootLookCentralOptions _options;
        private readonly ICentralKeyProvider _keys;
        private readonly ILogger? _logger;
        private readonly TimeProvider _time;
        private readonly JsonWebTokenHandler _handler = new();

        public CentralPassValidator(
            FootLookCentralOptions options,
            ICentralKeyProvider keys,
            ILogger<CentralPassValidator>? logger = null,
            TimeProvider? timeProvider = null)
        {
            _options = options;
            _keys = keys;
            _logger = logger;
            _time = timeProvider ?? TimeProvider.System;
        }

        public async Task<PassValidationResult> ValidateAsync(string? pass, CancellationToken cancellationToken = default)
        {
            var result = await ValidateCoreAsync(pass, cancellationToken);
            if (!result.IsValid)
            {
                // The reason only; never the pass itself.
                _logger?.LogInformation("FootLook central pass rejected: {Reason}", result.Failure);
            }

            return result;
        }

        private async Task<PassValidationResult> ValidateCoreAsync(string? pass, CancellationToken cancellationToken)
        {
            if (!_options.IsEnabled)
            {
                return PassValidationResult.Reject("central_mode_off");
            }

            if (string.IsNullOrWhiteSpace(pass) || pass.Length > MaxTokenLength || pass.Count(c => c == '.') != 2)
            {
                return PassValidationResult.Reject("malformed");
            }

            JsonWebToken parsed;
            try
            {
                parsed = new JsonWebToken(pass);
            }
            catch (Exception)
            {
                return PassValidationResult.Reject("malformed");
            }

            // Cheap checks on the still-unverified header, before any key is fetched or used.
            if (!string.Equals(parsed.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
            {
                return PassValidationResult.Reject("bad_algorithm");
            }

            var keyId = parsed.Kid;
            if (string.IsNullOrEmpty(keyId) || keyId.Length > MaxKeyIdLength)
            {
                return PassValidationResult.Reject("kid_missing");
            }

            var lookup = await _keys.GetKeyAsync(keyId, cancellationToken);
            if (lookup.Unavailable)
            {
                return new PassValidationResult(null, "keys_unavailable", Unavailable: true);
            }

            if (lookup.Key is null)
            {
                return PassValidationResult.Reject("unknown_signing_key");
            }

            // Lifetime is checked below against our own clock, so it is not delegated here.
            var validation = await _handler.ValidateTokenAsync(pass, new TokenValidationParameters
            {
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = lookup.Key,
                // Only RS256: never let the token pick a weaker or symmetric algorithm.
                ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },

                ValidateIssuer = true,
                ValidIssuer = _options.Issuer,

                ValidateAudience = true,
                ValidAudience = _options.ProjectId,

                ValidateLifetime = false,
            });

            if (!validation.IsValid || validation.SecurityToken is not JsonWebToken verified)
            {
                return PassValidationResult.Reject(Describe(validation.Exception));
            }

            return ExtractPass(verified);
        }

        private PassValidationResult ExtractPass(JsonWebToken token)
        {
            // The library already matched aud against the ProjectId; require it to be exactly that
            // and nothing else, so a pass that is also addressed to another audience is refused.
            var audiences = token.Audiences.ToList();
            if (audiences.Count != 1 || !string.Equals(audiences[0], _options.ProjectId, StringComparison.Ordinal))
            {
                return PassValidationResult.Reject("wrong_audience");
            }

            if (!TryGetSeconds(token, "exp", out var expires) ||
                !TryGetSeconds(token, "nbf", out var notBefore) ||
                !TryGetSeconds(token, "iat", out var issuedAt))
            {
                return PassValidationResult.Reject("time_claims_missing");
            }

            var now = _time.GetUtcNow();
            var skew = TimeSpan.FromSeconds(Math.Clamp(_options.ClockSkewSeconds, 0, MaxClockSkewSeconds));

            if (expires - issuedAt > MaxLifetime || expires <= issuedAt)
            {
                return PassValidationResult.Reject("lifetime_invalid");
            }

            if (now >= expires + skew)
            {
                return PassValidationResult.Reject("expired");
            }

            if (now < notBefore - skew)
            {
                return PassValidationResult.Reject("not_yet_valid");
            }

            if (issuedAt > now + skew)
            {
                return PassValidationResult.Reject("iat_in_future");
            }

            var subject = GetString(token, "sub")?.Trim();
            if (string.IsNullOrEmpty(subject) || subject.Length > MaxSubjectLength)
            {
                return PassValidationResult.Reject("subject_missing");
            }

            var email = GetString(token, "email")?.Trim();
            if (string.IsNullOrEmpty(email) || email.Length > MaxEmailLength)
            {
                return PassValidationResult.Reject("email_missing");
            }

            var role = GetString(token, "role");
            if (!string.Equals(role, OwnerRole, StringComparison.Ordinal) && !string.Equals(role, MemberRole, StringComparison.Ordinal))
            {
                return PassValidationResult.Reject("role_invalid");
            }

            var jwtId = GetString(token, "jti");
            if (string.IsNullOrWhiteSpace(jwtId) || jwtId.Length > MaxJwtIdLength)
            {
                return PassValidationResult.Reject("jti_missing");
            }

            // name is optional: fall back to the part of the address before the @ (or the whole
            // value when it has none), the same convention the local sign-in flows use.
            var displayName = GetString(token, "name")?.Trim();
            if (string.IsNullOrEmpty(displayName))
            {
                var at = email.IndexOf('@');
                displayName = at > 0 ? email[..at] : email;
            }

            if (displayName.Length > MaxDisplayNameLength)
            {
                displayName = displayName[..MaxDisplayNameLength];
            }

            return PassValidationResult.Ok(new CentralPass(subject, email, displayName, role!, jwtId, expires));
        }

        private static bool TryGetSeconds(JsonWebToken token, string claim, out DateTimeOffset value)
        {
            value = default;
            if (!token.TryGetPayloadValue<long>(claim, out var seconds) ||
                seconds < 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds() / 2)
            {
                return false;
            }

            value = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }

        private static string? GetString(JsonWebToken token, string claim) =>
            token.TryGetPayloadValue<string>(claim, out var value) ? value : null;

        private static string Describe(Exception? exception) => exception switch
        {
            SecurityTokenInvalidAudienceException => "wrong_audience",
            SecurityTokenInvalidIssuerException => "wrong_issuer",
            SecurityTokenSignatureKeyNotFoundException => "unknown_signing_key",
            SecurityTokenInvalidSignatureException => "bad_signature",
            SecurityTokenInvalidAlgorithmException => "bad_algorithm",
            SecurityTokenException => "invalid",
            _ => "invalid",
        };
    }
}
