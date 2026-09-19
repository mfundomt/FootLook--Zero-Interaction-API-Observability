using System.Globalization;
using System.Net.Mail;
using FootLook.Core.Models;
using FootLook.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Core.Security
{
    /// <summary>Outcome of <see cref="MicrosoftIdTokenValidator.ValidateAsync"/>.</summary>
    /// <param name="Identity">The proven identity, or null when the token was rejected.</param>
    /// <param name="Failure">Short reason code for logs and tests - never sent to clients.</param>
    /// <param name="Unavailable">True when the token could not be checked at all (signing keys could not be fetched), as opposed to being bad.</param>
    public sealed record MicrosoftTokenValidationResult(MicrosoftIdentity? Identity, string? Failure, bool Unavailable = false)
    {
        public bool IsValid => Identity is not null;

        public static MicrosoftTokenValidationResult Ok(MicrosoftIdentity identity) => new(identity, null);

        public static MicrosoftTokenValidationResult Reject(string reason) => new(null, reason);
    }

    /// <summary>
    /// Validates a Microsoft (Entra ID) v2.0 ID token posted by the sign-in SPA and turns it
    /// into a <see cref="MicrosoftIdentity"/>.
    /// <para>
    /// Signing keys come from the OpenID Connect metadata document through a cached
    /// <see cref="IConfigurationManager{T}"/>, which can be replaced (constructor) so tests
    /// can sign tokens with a locally generated key. The "common" metadata's issuer is a
    /// template ("https://login.microsoftonline.com/{tenantid}/v2.0"), so the issuer check is
    /// custom: it must equal that pattern filled in with the token's OWN tid claim (a GUID).
    /// The signature, RS256-only algorithm, lifetime (small skew), audience (our client id)
    /// and "ver == 2.0" are all enforced too.
    /// </para>
    /// <para>
    /// Lives in FootLook.Core because it needs only Microsoft.IdentityModel.* packages that
    /// Core already pulls in through JwtBearer - no new dependency reaches the NuGet package.
    /// </para>
    /// </summary>
    public sealed class MicrosoftIdTokenValidator
    {
        /// <summary>Tenant id Microsoft uses for every personal (consumer) account.</summary>
        public const string PersonalTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

        private const int MaxTokenLength = 16 * 1024;
        private const int MaxEmailLength = 320;
        private const int MaxDisplayNameLength = 200;
        private const int MaxSubjectLength = 128;

        private readonly FootLookMicrosoftOptions _options;
        private readonly IConfigurationManager<OpenIdConnectConfiguration> _configuration;
        private readonly ILogger? _logger;
        private readonly JsonWebTokenHandler _handler = new();

        public MicrosoftIdTokenValidator(
            FootLookMicrosoftOptions options,
            ILogger<MicrosoftIdTokenValidator>? logger = null,
            IConfigurationManager<OpenIdConnectConfiguration>? configurationManager = null)
        {
            _options = options;
            _logger = logger;
            _configuration = configurationManager ?? new ConfigurationManager<OpenIdConnectConfiguration>(
                options.MetadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = true });
        }

        public static string ExpectedIssuer(string tenantId) => $"https://login.microsoftonline.com/{tenantId}/v2.0";

        public async Task<MicrosoftTokenValidationResult> ValidateAsync(string? idToken, CancellationToken cancellationToken = default)
        {
            var result = await ValidateCoreAsync(idToken, cancellationToken);
            if (!result.IsValid)
            {
                _logger?.LogInformation("Microsoft ID token rejected: {Reason}", result.Failure);
            }

            return result;
        }

        private async Task<MicrosoftTokenValidationResult> ValidateCoreAsync(string? idToken, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > MaxTokenLength)
            {
                return MicrosoftTokenValidationResult.Reject("malformed");
            }

            JsonWebToken parsed;
            try
            {
                parsed = new JsonWebToken(idToken);
            }
            catch (Exception)
            {
                return MicrosoftTokenValidationResult.Reject("malformed");
            }

            // Cheap checks on the still-unverified payload first. They only decide whether
            // it is worth verifying at all; every value used later is re-read from the token
            // after the signature has been checked.
            if (!TryGetTenantId(parsed, out _))
            {
                return MicrosoftTokenValidationResult.Reject("tid_missing_or_not_guid");
            }

            if (!string.Equals(GetString(parsed, "ver"), "2.0", StringComparison.Ordinal))
            {
                return MicrosoftTokenValidationResult.Reject("not_v2");
            }

            TokenValidationResult validation;
            try
            {
                validation = await ValidateSignatureAndClaimsAsync(idToken, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
            {
                // Signing keys could not be fetched or parsed: not the caller's fault.
                _logger?.LogWarning(ex, "Could not load Microsoft signing keys from {Metadata}", _options.MetadataAddress);
                return new MicrosoftTokenValidationResult(null, "metadata_unavailable", Unavailable: true);
            }

            if (!validation.IsValid || validation.SecurityToken is not JsonWebToken verified)
            {
                return MicrosoftTokenValidationResult.Reject(Describe(validation.Exception));
            }

            return ExtractIdentity(verified);
        }

        private async Task<TokenValidationResult> ValidateSignatureAndClaimsAsync(string idToken, CancellationToken cancellationToken)
        {
            var result = await _handler.ValidateTokenAsync(idToken, await BuildParametersAsync(cancellationToken));

            // Microsoft rotates its signing keys. A token signed with a key we have not seen
            // yet triggers one (rate-limited) metadata refresh and a single retry.
            if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                _configuration.RequestRefresh();
                result = await _handler.ValidateTokenAsync(idToken, await BuildParametersAsync(cancellationToken));
            }

            return result;
        }

        private async Task<TokenValidationParameters> BuildParametersAsync(CancellationToken cancellationToken)
        {
            var configuration = await _configuration.GetConfigurationAsync(cancellationToken);
            var skew = TimeSpan.FromSeconds(Math.Max(0, _options.ClockSkewSeconds));

            return new TokenValidationParameters
            {
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = configuration.SigningKeys,
                // Only RS256: never let the token pick a weaker or symmetric algorithm.
                ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },

                ValidateAudience = true,
                ValidAudience = _options.ClientId,

                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = skew,

                ValidateIssuer = true,
                IssuerValidator = ValidateIssuer,
            };
        }

        /// <summary>
        /// The metadata's issuer is a template, so the configured-issuer comparison the
        /// library would do cannot work. Require issuer == https://login.microsoftonline.com/{tid}/v2.0
        /// with the tid taken from this same (already signature-checked) token.
        /// </summary>
        private static string ValidateIssuer(string issuer, SecurityToken token, TokenValidationParameters _)
        {
            if (token is not JsonWebToken jwt || !TryGetTenantId(jwt, out var rawTenantId))
            {
                throw new SecurityTokenInvalidIssuerException("Token has no usable tid claim.") { InvalidIssuer = issuer };
            }

            if (!string.Equals(issuer, ExpectedIssuer(rawTenantId), StringComparison.Ordinal))
            {
                throw new SecurityTokenInvalidIssuerException("Issuer does not match the token's tenant.") { InvalidIssuer = issuer };
            }

            return issuer;
        }

        private MicrosoftTokenValidationResult ExtractIdentity(JsonWebToken token)
        {
            if (!TryGetTenantId(token, out var rawTenantId) || !Guid.TryParseExact(rawTenantId, "D", out var tenantGuid))
            {
                return MicrosoftTokenValidationResult.Reject("tid_missing_or_not_guid");
            }

            if (!string.Equals(GetString(token, "ver"), "2.0", StringComparison.Ordinal))
            {
                return MicrosoftTokenValidationResult.Reject("not_v2");
            }

            if (_options.MaxIdTokenAgeMinutes > 0)
            {
                var issuedAt = GetIssuedAt(token);
                if (issuedAt is null)
                {
                    return MicrosoftTokenValidationResult.Reject("iat_missing");
                }

                var now = DateTimeOffset.UtcNow;
                var skew = TimeSpan.FromSeconds(Math.Max(0, _options.ClockSkewSeconds));
                if (issuedAt.Value > now + skew)
                {
                    return MicrosoftTokenValidationResult.Reject("iat_in_future");
                }

                if (now - issuedAt.Value > TimeSpan.FromMinutes(_options.MaxIdTokenAgeMinutes) + skew)
                {
                    return MicrosoftTokenValidationResult.Reject("token_too_old");
                }
            }

            // Subject: oid (stable per user per tenant) when present, else sub.
            var subject = GetString(token, "oid");
            if (string.IsNullOrWhiteSpace(subject))
            {
                subject = GetString(token, "sub");
            }

            subject = subject?.Trim();
            if (string.IsNullOrEmpty(subject) || subject.Length > MaxSubjectLength)
            {
                return MicrosoftTokenValidationResult.Reject("subject_missing");
            }

            // Email is only contact/display data - never an identity key. preferred_username
            // is accepted as a fallback only when it actually looks like an address (for
            // some accounts it is a phone number or an opaque name).
            var email = NormalizeEmail(GetString(token, "email")) ?? NormalizeEmail(GetString(token, "preferred_username"));
            if (email is null)
            {
                return MicrosoftTokenValidationResult.Reject("email_missing");
            }

            var displayName = GetString(token, "name")?.Trim();
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = email[..email.IndexOf('@')];
            }
            else if (displayName.Length > MaxDisplayNameLength)
            {
                displayName = displayName[..MaxDisplayNameLength];
            }

            var tenantId = tenantGuid.ToString("D", CultureInfo.InvariantCulture);
            var accountType = string.Equals(tenantId, PersonalTenantId, StringComparison.Ordinal) ? "personal" : "work";

            return MicrosoftTokenValidationResult.Ok(new MicrosoftIdentity(tenantId, subject, email, displayName, accountType));
        }

        private static bool TryGetTenantId(JsonWebToken token, out string tenantId)
        {
            tenantId = GetString(token, "tid") ?? string.Empty;
            return Guid.TryParseExact(tenantId, "D", out _);
        }

        private static string? GetString(JsonWebToken token, string claim) =>
            token.TryGetPayloadValue<string>(claim, out var value) ? value : null;

        private static DateTimeOffset? GetIssuedAt(JsonWebToken token) =>
            token.TryGetPayloadValue<long>("iat", out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

        private static string? NormalizeEmail(string? value)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxEmailLength ||
                !MailAddress.TryCreate(trimmed, out var parsed) ||
                !string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase) ||
                !parsed.Host.Contains('.'))
            {
                return null;
            }

            return trimmed.ToLowerInvariant();
        }

        private static string Describe(Exception? exception) => exception switch
        {
            SecurityTokenExpiredException => "expired",
            SecurityTokenNotYetValidException => "not_yet_valid",
            SecurityTokenInvalidAudienceException => "wrong_audience",
            SecurityTokenInvalidIssuerException => "wrong_issuer",
            SecurityTokenSignatureKeyNotFoundException => "unknown_signing_key",
            SecurityTokenInvalidSignatureException => "bad_signature",
            SecurityTokenInvalidAlgorithmException => "bad_algorithm",
            SecurityTokenException => "invalid",
            null => "invalid",
            _ => "invalid",
        };
    }
}
